﻿/*
Project Orleans Cloud Service SDK ver. 1.0
 
Copyright (c) Microsoft Corporation
 
All rights reserved.
 
MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and 
associated documentation files (the ""Software""), to deal in the Software without restriction,
including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense,
and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED *AS IS*, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO
THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS
OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT,
TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
*/

using Orleans.Runtime;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Orleans.TestingHost;
using System.Reflection;
using System.Globalization;
using UnitTests.Tester;
using Orleans.Runtime.Configuration;
using System.Net;
using System.Net.Sockets;
using Orleans;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Tests.GeoClusterTests
{
    public class TestingClusterHost   
    {
        protected readonly Dictionary<string, ClusterInfo> Clusters;

        public TestingClusterHost() : base()
        {
            Clusters = new Dictionary<string, ClusterInfo>();

            UnitTestSiloHost.CheckForAzureStorage();
        }

        protected struct ClusterInfo
        {
            public List<SiloHandle> Silos;  // currently active silos
            public int SequenceNumber; // we number created clusters in order of creation
            public int MaxSilos; 
        }

        private static readonly string ConfigPrefix =
              Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);

        public static string GetConfigFile(string fileName)
        {
            return Path.Combine(ConfigPrefix, fileName);
        }
        public static void WriteLog(string format, params object[] args)
        {
            Console.WriteLine(format, args);
        }


        #region Default Cluster and Client Configuration

        private static int GetPortBase(int clusternumber)
        {
            return 21000 + (clusternumber + 1) * 100 + 11;
        }
        private static int GetProxyBase(int clusternumber)
        {
            return 22000 + (clusternumber + 2) * 100 + 22;
        }
        private static int DetermineGatewayPort(int clusternumber, int clientnumber)
        {
            return GetProxyBase(clusternumber) + clientnumber % 3;
        }

        public void AdjustClusterPortDefaults(ClusterConfiguration c, int clusternumber, int maxsilos = 5)
        {

            var portbase = GetPortBase(clusternumber);
            var proxybase = GetProxyBase(clusternumber);

            c.Globals.SeedNodes.Clear();
            c.Globals.SeedNodes.Add(new IPEndPoint(IPAddress.Loopback, portbase));
            NodeOverride(c, "Primary", portbase, proxybase);
            for (int i = 1; i < maxsilos; i++)
                NodeOverride(c, "Secondary_" + i, portbase + i, proxybase + i);
        }

        private void NodeOverride(ClusterConfiguration config, string siloName, int port, int proxyGatewayEndpoint = 0)
        {
            NodeConfiguration nodeConfig = config.GetConfigurationForNode(siloName);
            nodeConfig.HostNameOrIPAddress = "loopback";
            nodeConfig.Port = port;
            nodeConfig.DefaultTraceLevel = config.Defaults.DefaultTraceLevel;
            nodeConfig.PropagateActivityId = config.Defaults.PropagateActivityId;
            nodeConfig.BulkMessageLimit = config.Defaults.BulkMessageLimit;
            nodeConfig.ProxyGatewayEndpoint = new IPEndPoint(IPAddress.Loopback, proxyGatewayEndpoint);
            config.Overrides[siloName] = nodeConfig;
        }
     
        #endregion

        #region Cluster Creation


        public void NewCluster(string globalserviceid, string clusterid, int numSilos, Action<ClusterConfiguration> customizer = null, int maxsilos = 5)
        {
            if (numSilos > maxsilos)
                throw new ArgumentException();

            lock (Clusters)
            {
                WriteLog("Starting Cluster {0}...", clusterid);
                var mycount = Clusters.Count;

                Action<ClusterConfiguration> configurationcustomizer = (config) =>
                    {
                        // configure ports
                        AdjustClusterPortDefaults(config, mycount, maxsilos);

                        // configure multi-cluster network
                        config.Globals.GlobalServiceId = globalserviceid;
                        config.Globals.ClusterId = clusterid;
                        config.Globals.MaxMultiClusterGateways = 2;
                        config.Globals.DefaultMultiCluster = null;

                        config.Globals.GossipChannels = new List<Orleans.Runtime.Configuration.GlobalConfiguration.GossipChannelConfiguration>(1) { 
                          new Orleans.Runtime.Configuration.GlobalConfiguration.GossipChannelConfiguration()
                          {
                              ChannelType = Orleans.Runtime.Configuration.GlobalConfiguration.GossipChannelType.AzureTable,
                              ConnectionString = StorageTestConstants.DataConnectionString
                          }};


                        // add custom configurations
                        if (customizer != null)
                            customizer(config);
                    };

                var silohandles = new SiloHandle[numSilos];

                var primaryOption = new TestingSiloOptions
                {
                    StartClient = false,
                    AutoConfigNodeSettings = false,
                    SiloName = "Primary",
                    ConfigurationCustomizer = configurationcustomizer
                };
                silohandles[0] = TestingSiloHost.StartOrleansSilo(null, Silo.SiloType.Primary, primaryOption, 0);

                Parallel.For(1, numSilos, i =>
                {
                    var options = new TestingSiloOptions
                    {
                        StartClient = false,
                        AutoConfigNodeSettings = false,
                        SiloName = "Secondary_" + i,
                        ConfigurationCustomizer = configurationcustomizer
                    };

                    silohandles[i] = TestingSiloHost.StartOrleansSilo(null, Silo.SiloType.Secondary, options, i);
                });

                string clusterId = silohandles[0].Silo.GlobalConfig.ClusterId;

                Clusters[clusterId] = new ClusterInfo
                {
                    Silos = silohandles.ToList(),
                    SequenceNumber = mycount,
                    MaxSilos = maxsilos
                };

                WriteLog("Cluster {0} started.", clusterId);
            }
        }

        public void AddSiloToCluster(string clusterId, string siloName, Action<ClusterConfiguration> customizer = null)
        {
            var clusterinfo = Clusters[clusterId];

            if (clusterinfo.Silos.Count >= clusterinfo.MaxSilos)
                Assert.Fail("Cannot create more silos");

            var options = new TestingSiloOptions
            {
                StartClient = false,
                AutoConfigNodeSettings = false,
                SiloName = siloName, 
                ConfigurationCustomizer = customizer
            };

            var silo = TestingSiloHost.StartOrleansSilo(null, Silo.SiloType.Secondary, options, clusterinfo.Silos.Count);
        }

      

        public void StopAllClientsAndClusters()
        {
            WriteLog("Stopping All Clients and Clusters...");
            StopAllClients();
            StopAllClusters();
            WriteLog("All Clients and Clusters Are Stopped.");
        }

        public void StopAllClusters()
        {
            lock (Clusters)
            {
                Parallel.ForEach(Clusters.Keys, key =>
                {
                    var info = Clusters[key];
                    Parallel.For(1, info.Silos.Count, i => TestingSiloHost.StopSilo(info.Silos[i]));
                    TestingSiloHost.StopSilo(info.Silos[0]);
                });
                Clusters.Clear();
            }
        }

        #endregion

        #region client wrappers

        private readonly List<AppDomain> activeClients = new List<AppDomain>();

        public class ClientWrapperBase : MarshalByRefObject {

            public string Name { get; private set; }

            public ClientWrapperBase(string name, int gatewayport)
            {
                this.Name = name;

                Console.WriteLine("Initializing client {0} in AppDomain {1}", name, AppDomain.CurrentDomain.FriendlyName);

                ClientConfiguration config = null;
                try
                {
                    config = ClientConfiguration.LoadFromFile("ClientConfigurationForTesting.xml");
                }
                catch (Exception) { }

                if (config == null)
                {
                    Assert.Fail("Error loading client configuration file");
                }
                config.GatewayProvider = ClientConfiguration.GatewayProviderType.Config;
                config.Gateways.Clear();
                config.Gateways.Add(new IPEndPoint(IPAddress.Loopback, gatewayport));

                GrainClient.Initialize(config);
            }
            
        }

        // Create a client, loaded in a new app domain.
        public T NewClient<T>(string ClusterId, int ClientNumber) where T: ClientWrapperBase
        {
            var ci = Clusters[ClusterId];
            var name = string.Format("Client-{0}-{1}", ClusterId, ClientNumber);
            var gatewayport = DetermineGatewayPort(ci.SequenceNumber, ClientNumber);
       
            var clientArgs = new object[] { name, gatewayport };
            var setup = new AppDomainSetup { ApplicationBase = Environment.CurrentDirectory };
            var clientDomain = AppDomain.CreateDomain(name, null, setup);

            T client = (T)clientDomain.CreateInstanceFromAndUnwrap(
                    Assembly.GetExecutingAssembly().Location, typeof(T).FullName, false,
                    BindingFlags.Default, null, clientArgs, CultureInfo.CurrentCulture,
                    new object[] { });

            lock (activeClients)
            {
                activeClients.Add(clientDomain);
            }

            return client;
        }

        public void StopAllClients()
        {
            lock (activeClients)
            {
                foreach (var client in activeClients)
                {
                    try
                    {
                        AppDomain.Unload(client);
                    }
                    catch (Exception e)
                    {
                        Console.WriteLine(e);
                        throw;
                    }
                }
                activeClients.Clear();
            }
        }

        #endregion

        #region Cluster Config


        public void BlockAllClusterCommunication(string from, string to)
        {
            foreach (var silo in Clusters[from].Silos)
                foreach (var dest in Clusters[to].Silos)
                    silo.Silo.TestHookup.BlockSiloCommunication(dest.Endpoint, 100);
        }

        public void UnblockAllClusterCommunication(string from)
        {
            foreach (var silo in Clusters[from].Silos)
                    silo.Silo.TestHookup.UnblockSiloCommunication();
        }

  
        private SiloHandle GetActiveSiloInClusterByName(string clusterId, string siloName)
        {
            if (Clusters[clusterId].Silos == null) return null;
            return Clusters[clusterId].Silos.Find(s => s.Name == siloName);
        }
        #endregion
    }
}