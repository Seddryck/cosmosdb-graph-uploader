﻿namespace CosmosDbGraphUploader.Console
{
    using Microsoft.Azure.Documents;
    using Microsoft.Azure.Documents.Client;
    using Microsoft.Azure.Graphs;
    using Newtonsoft.Json;
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Configuration;
    using System.IO;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;

    public class Program
    {
        private static Dictionary<string, Node> nodes;
        private static Dictionary<string, Edge> edges;
        private static GraphConnection connection;
        private static DocumentClient client;
        private static DocumentCollection collection;

        private static readonly ConcurrentBag<string> errors = new ConcurrentBag<string>();

        private static int counterUploaded;
        private static int counterExist;

        private static readonly object lockObject = new object();

        static void Main()
        {
            try
            {
                string endpointUrl = ConfigurationManager.AppSettings["endpoint"];
                string primaryKey = ConfigurationManager.AppSettings["authKey"];
                string databaseId = ConfigurationManager.AppSettings["database"];
                string collectionName = ConfigurationManager.AppSettings["collection"];

                // Let's check if the database and collection exist, or if they need to be created
                SetupGraphDb(endpointUrl, primaryKey, databaseId, collectionName).Wait();

                // Time to upload the graph
                Console.WriteLine("Uploading the graph now...");

                var database = client.CreateDatabaseQuery("SELECT * FROM d WHERE d.id = \"" + databaseId + "\"").AsEnumerable().FirstOrDefault();
                var collections = (List<DocumentCollection>)client.CreateDocumentCollectionQuery(database.SelfLink).ToList();
                collection = collections.FirstOrDefault(x => x.Id == collectionName);

                connection = new GraphConnection(client, collection);
                int maxTasks;
                int.TryParse(ConfigurationManager.AppSettings["MaxTasks"], out maxTasks);

                // Processing GraphConfig
                string configPath = ConfigurationManager.AppSettings["GraphConfigFile"];
                string graphConfigText = File.ReadAllText(configPath);
                var graphConfig = JsonConvert.DeserializeObject<GraphConfig>(graphConfigText);

                nodes = new Dictionary<string, Node>();
                edges = new Dictionary<string, Edge>();
                foreach (var node in graphConfig.Nodes)
                {
                    nodes.Add(node.Name, node);
                }

                foreach (var edge in graphConfig.Edges)
                {
                    edges.Add(edge.Name, edge);
                }

                // Upload Nodes
                Console.WriteLine("\nStarting Node upload");
                nodes.AsyncParallelForEach(async node => await UploadNode(node.Value), maxTasks).Wait();
                Console.WriteLine("\nUploaded Nodes");

                counterUploaded = counterExist = 0;

                // Upload edges
                Console.WriteLine("\nStarting Edge upload");
                edges.AsyncParallelForEach(async edge => await UploadEdge(edge.Value), maxTasks).Wait();
                Console.WriteLine("\nUploaded Edges");

                File.WriteAllLines(ConfigurationManager.AppSettings["ErrorLogPath"], errors);
                Console.WriteLine("Graph uploaded ! " + (errors.Count == 0 ? "" : ("But there were " + errors.Count + " errors. Please check the log file.")));

                Console.WriteLine("");
                Console.WriteLine("You can now shut down this application and close the solution.");
                Console.WriteLine("Next, you can build the GraphExplorer solution, and run it to see the web front end for visualizing graph data.");
            }
            catch (Exception ex)
            {
                Console.WriteLine("Something's not right here. Details - " + ex.Message);
            }

            Console.Read();
        }

        static async Task<bool> SetupGraphDb(string endpointUrl, string primaryKey, string databaseId, string collectionName)
        {
            try
            {
                client = new DocumentClient(new Uri(endpointUrl), primaryKey);

                try
                {
                    await client.ReadDatabaseAsync(UriFactory.CreateDatabaseUri(databaseId));
                    Console.WriteLine("Verified that database exists...");
                }
                catch (DocumentClientException documentClientException)
                {
                    if (documentClientException.Error?.Code == "NotFound")
                    {
                        Console.WriteLine("Your Database, \"" + databaseId + "\" does not exist. Creating it...");
                        await client.CreateDatabaseIfNotExistsAsync(new Database {Id = databaseId});
                        Console.WriteLine("Created Database!");
                    }
                    else
                    {
                        throw;
                    }
                }

                try
                {
                    await client.ReadDocumentCollectionAsync(UriFactory.CreateDocumentCollectionUri(databaseId, collectionName));
                    Console.WriteLine("Verified that collection exists...");
                }
                catch (DocumentClientException documentClientException)
                {
                    if (documentClientException.Error?.Code == "NotFound")
                    {
                        Console.WriteLine("Your Collection, \"" + collectionName + "\" does not exist. Creating it...");
                        await client.CreateDocumentCollectionIfNotExistsAsync(UriFactory.CreateDatabaseUri(databaseId), new DocumentCollection { Id = collectionName });
                        Console.WriteLine("Created Collection!");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("Ran into an issue creating/checking CosmosDB account, database or collection. Please check all values are specified correctly in the settings file. \nDetails - " + ex.Message);
                return false;
            }

            return true;
        }

        private static async Task<bool> UploadNode(Node node)
        {
            var graphCommand = new GraphCommand(connection);

            // Get all existing edges so we can check if the edge already exist before inserting
            var getAllNodeInternalInstruction = graphCommand.g().V().Values("NodeId_Internal");
            var allNodesInternalIds = await getAllNodeInternalInstruction.NextAsync();

            // Get list of all files in the directory for this node
            var files = Directory.GetFiles(node.PathToData).ToList();
            foreach (var filePath in files)
            {
                var lines = File.ReadAllLines(filePath).ToList();

                foreach (var line in lines)
                {
                    var values = line.Split('\t').ToList();
                    try
                    {
                        var insertNodeInstruction = graphCommand.g().AddV(node.Name);
                        var nodeInternalId = node.Name;

                        for (int i = 0; i < node.Attributes.Count; i++)
                        {
                            insertNodeInstruction = insertNodeInstruction.Property(node.Attributes[i], values[i]);
                            if (node.PrimaryAttributes.Contains(node.Attributes[i]))
                            {
                                nodeInternalId += values[i];
                            }
                        }

                        insertNodeInstruction = insertNodeInstruction.Property("NodeId_Internal", nodeInternalId);

                        if (!allNodesInternalIds.Contains(nodeInternalId))
                        {
                            List<string> resultInsert = await insertNodeInstruction.NextAsync();
                            if (resultInsert.Count == 0 || resultInsert[0] == "[]")
                            {
                                throw new Exception("Could not insert node");
                            }

                            Interlocked.Increment(ref counterUploaded);
                        }
                        else
                        {
                            Interlocked.Increment(ref counterExist);
                        }

                        Console.Write("\rUploaded " + counterUploaded + " nodes and found " + counterExist + " existing nodes");
                    }
                    catch (Exception ex)
                    {
                        errors.Add(node.Name + " (" + ex + ") : " + JsonConvert.SerializeObject(values) + "\n");
                    }
                }
            }

            return true;
        }


        private static async Task<bool> UploadEdge(Edge edge)
        {
            var sourceNodePrimaryKey = nodes[edge.SourceNode].NodeIdAttribute;
            var destNodePrimaryKey = nodes[edge.DestinationNode].NodeIdAttribute;

            var graphCommand = new GraphCommand(connection);

            // Get all existing edges so we can check if the edge already exist before inserting
            var getAllEdgeInternalInstruction = graphCommand.g().E().Values("EdgeId_Internal");
            var allEdgesInternalIds = await getAllEdgeInternalInstruction.NextAsync();

            // Get list of all files in the directory for this edge
            var files = Directory.GetFiles(edge.PathToData).ToList();
            foreach (string filePath in files)
            {
                var lines = File.ReadAllLines(filePath).ToList();
                foreach (var line in lines)
                {
                    List<string> values = line.Split('\t').ToList();
                    Dictionary<string, string> keyval = new Dictionary<string, string>();

                    for (var i = 0; i < edge.Attributes.Count; i++)
                    {
                        keyval.Add(edge.Attributes[i], values[i]);
                    }

                    try
                    {
                        var insertEdgeInstruction = graphCommand.g().V().Has(sourceNodePrimaryKey, keyval[sourceNodePrimaryKey]).AddE(edge.SourceNode + edge.DestinationNode);
                        var edgeInternalId = edge.Name;

                        foreach (var item in keyval)
                        {
                            if (item.Key != sourceNodePrimaryKey && item.Key != destNodePrimaryKey)
                            {
                                insertEdgeInstruction = insertEdgeInstruction.Property(item.Key, item.Value);
                            }

                            if (edge.PrimaryAttributes.Contains(item.Key))
                            {
                                edgeInternalId += item.Value;
                            }
                        }

                        insertEdgeInstruction = insertEdgeInstruction.Property("EdgeId_Internal", edgeInternalId).To(graphCommand.g().V().Has(destNodePrimaryKey, keyval[destNodePrimaryKey]));

                        if (!allEdgesInternalIds.Contains(edgeInternalId))
                        {
                            var resultInsert = await insertEdgeInstruction.NextAsync();

                            if (resultInsert.Count == 0 || resultInsert[0] == "[]")
                            {

                                throw new Exception("Could not insert edge");
                            }
                            Interlocked.Increment(ref counterUploaded);
                        }
                        else
                        {
                            Interlocked.Increment(ref counterExist);
                        }

                        Console.Write("\rUploaded " + counterUploaded + " edges and found " + counterExist + " existing edges");
                    }
                    catch (Exception ex)
                    {
                        errors.Add(edge.Name + " (" + ex + ") : " + JsonConvert.SerializeObject(keyval) + "\n");
                    }
                }
            }

            return true;
        }
    }

    class Entity
    {
        public string Name { get; set; }

        public string PathToData { get; set; }

        public List<string> Attributes { get; set; }

        public List<string> PrimaryAttributes { get; set; }
    }

    class Edge : Entity
    {
        public string SourceNode { get; set; }

        public string DestinationNode { get; set; }
    }

    class Node : Entity
    {
        public string NodeIdAttribute { get; set; }
    }

    class GraphConfig
    {
        public List<Node> Nodes { get; set; }

        public List<Edge> Edges { get; set; }
    }
}