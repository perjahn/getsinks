using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace getsinks
{
    class Sink
    {
        public string NodeId { get; set; } = string.Empty;  // org/folder/project ID
        public string NodeName { get; set; } = string.Empty;  // org/folder/project Name
        public string Name { get; set; } = string.Empty;
        public string Destination { get; set; } = string.Empty;
        public string Filter { get; set; } = string.Empty;
    }

    class Program
    {
        static async Task<int> Main(string[] args)
        {
            if (args.Length != 1)
            {
                Console.WriteLine(
                    "getsinks 0.001 gamma.\n" +
                    "\n" +
                    "Usage: getsinks <access token>\n" +
                    "\n" +
                    "One way to retrive a GCP access token is to run: \"gcloud auth application-default print-access-token\"");
                return 1;
            }

            var accessToken = args[0];

            List<Sink> sinks;
            try
            {
                sinks = await GetSinks(accessToken);
            }
            catch (Exception ex)
            {
                Log($"Couldn't get sinks: {ex.Message}", ConsoleColor.Red);
                return 1;
            }

            if (sinks.Count == 0)
            {
                Console.WriteLine("No sinks found.");
                return 1;
            }
            Console.WriteLine($"Got {sinks.Count} sinks.");

            ShowSinks(sinks.ToArray());

            return 0;
        }

        static void ShowSinks(Sink[] sinks)
        {
            var rows = sinks.Select(s => new[] { s.NodeName, s.NodeId, s.Name, s.Destination, GetFirst(s.Filter, 20) }).ToArray();

            Array.Sort(rows, Compare);

            var header = new string[][] { new[] { "NodeName", "NodeId", "SinkName", "Destination", "Filter" } };

            rows = header.Concat(rows).ToArray();


            int[] maxwidths = GetMaxWidths(rows);

            for (int row = 0; row < rows.Length; row++)
            {
                var output = new StringBuilder();

                for (int col = 0; col < rows[row].Length; col++)
                {
                    if (col > 0)
                    {
                        output.Append("  ");
                    }
                    output.AppendFormat("{0,-" + maxwidths[col] + "}", rows[row][col]);
                }

                Console.WriteLine(output.ToString().TrimEnd());
            }
        }

        static string GetFirst(string text, int chars)
        {
            return text.Length < chars ? text : text.Substring(0, chars - 3) + "...";
        }

        static int Compare(string[] arr1, string[] arr2)
        {
            foreach (var element in arr1.Zip(arr2))
            {
                int result = string.Compare(element.First, element.Second, StringComparison.OrdinalIgnoreCase);
                if (result < 0)
                {
                    return -1;
                }
                if (result > 0)
                {
                    return 1;
                }
            }
            if (arr1.Length < arr2.Length)
            {
                return -1;
            }
            if (arr1.Length > arr2.Length)
            {
                return 1;
            }

            return 0;
        }

        static int[] GetMaxWidths(string[][] rows)
        {
            if (rows.Length == 0)
            {
                return Array.Empty<int>();
            }

            int[] maxwidths = new int[rows[0].Length];

            for (var row = 0; row < rows.Length; row++)
            {
                for (var col = 0; col < rows[0].Length && col < rows[row].Length; col++)
                {
                    if (row == 0)
                    {
                        maxwidths[col] = rows[row][col].Length;
                    }
                    else
                    {
                        if (rows[row][col].Length > maxwidths[col])
                        {
                            maxwidths[col] = rows[row][col].Length;
                        }
                    }
                }
            }

            return maxwidths;
        }

        static async Task<List<Sink>> GetSinks(string accessToken)
        {
            var client = new HttpClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            client.BaseAddress = new Uri("https://cloudresourcemanager.googleapis.com");

            var loggingClient = new HttpClient();
            loggingClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            loggingClient.BaseAddress = new Uri("https://logging.googleapis.com");

            var tasks = new[] {
                GetGenericSinks(client, loggingClient, "/v3/organizations:search", "organizations", "organization"),
                GetGenericSinks(client, loggingClient, "/v3/folders:search", "folders", "folder"),
                GetGenericSinks(client, loggingClient, "/v3/projects:search", "projects", "project")};

            var sinkList = new List<Sink>();
            foreach (var task in tasks)
            {
                sinkList.AddRange(await task);
            }

            return sinkList;
        }

        static async Task<List<Sink>> GetGenericSinks(HttpClient client, HttpClient loggingClient, string url, string rootNodeName, string objectTypeName)
        {
            var sinkList = new List<Sink>();

            var result = await client.GetAsync(url);
            var content = await result.Content.ReadAsStringAsync();

            JObject root;
            JToken? message;
            if (TryParseJObject(content, out root) && result.StatusCode == HttpStatusCode.Unauthorized && (message = root["error"]?["message"]) != null)
            {
                Log($"Ignoring all {objectTypeName} sinks: {message.Value<string>()}", ConsoleColor.Yellow);
                return sinkList;
            }
            if (!result.IsSuccessStatusCode)
            {
                Log($"Ignoring all {objectTypeName} sinks, couldn't get {rootNodeName}, unsuccessful status code: '{url}' {result.StatusCode} >>>{content}<<<", ConsoleColor.Yellow);
                return sinkList;
            }
            JToken? value;
            if (!TryParseJObject(content, out root) || (value = root[rootNodeName]) == null || !(value is JArray jarray))
            {
                Log($"Ignoring all {objectTypeName} sinks, couldn't get {rootNodeName}, couldn't parse json: '{url}' {result.StatusCode} >>>{content}<<<", ConsoleColor.Yellow);
                return sinkList;
            }

            Console.WriteLine($"Got {jarray.Count} {rootNodeName}.");

            var tasks = new List<Task<List<Sink>>>();

            foreach (var jtoken in jarray)
            {
                if (jtoken is JObject jobject)
                {
                    tasks.Add(GetNodeSinks(loggingClient, jobject, objectTypeName));
                }
                else
                {
                    Log($"Ignoring invalid {objectTypeName}: {jtoken.ToString()}", ConsoleColor.Yellow);
                }
            }

            foreach (var task in tasks)
            {
                sinkList.AddRange(await task);
            }

            return sinkList;
        }

        static async Task<List<Sink>> GetNodeSinks(HttpClient client, JObject node, string objectTypeName)
        {
            var sinkList = new List<Sink>();

            JToken? value;
            string? name, displayName, state, destination, filter;

            if ((name = (value = node["name"])?.Value<string>()) == null || !(value is JValue))
            {
                Log($"Ignoring invalid {objectTypeName}, missing name: >>>{node.ToString()}<<<", ConsoleColor.Yellow);
                return sinkList;
            }
            string nodeId = name;

            if ((displayName = (value = node["displayName"])?.Value<string>()) == null || !(value is JValue))
            {
                Log($"Ignoring invalid {objectTypeName}, missing displayName: >>>{node.ToString()}<<<", ConsoleColor.Yellow);
                return sinkList;
            }
            string nodeName = displayName;

            string url = $"/v2/{nodeId}/sinks";
            var result = await client.GetAsync(url);
            var content = await result.Content.ReadAsStringAsync();

            if ((result.StatusCode == HttpStatusCode.NotFound || result.StatusCode == HttpStatusCode.Forbidden) && (state = node["state"]?.Value<string>()) != null)
            {
                Console.WriteLine($"Ignoring {objectTypeName}: '{nodeName}', {state}, {result.StatusCode}");
                return sinkList;
            }
            if (!result.IsSuccessStatusCode)
            {
                Log($"Ignoring {objectTypeName}, couldn't get sinks, unsuccessful status code: '{url}' {result.StatusCode} >>>{node}<<< >>>{content}<<<", ConsoleColor.Yellow);
                return sinkList;
            }
            if (!TryParseJObject(content, out JObject root) || (value = root["sinks"]) == null || !(value is JArray sinks))
            {
                Log($"Ignoring {objectTypeName}, couldn't get sinks, couldn't parse json: '{url}' {result.StatusCode} >>>{node}<<< >>>{content}<<<", ConsoleColor.Yellow);
                return sinkList;
            }

            foreach (var sink in sinks)
            {
                if (!(sink is JObject sinkObject))
                {
                    Log($"Ignoring invalid sink: {sink.ToString()}", ConsoleColor.Yellow);
                    continue;
                }

                if ((name = (value = sinkObject["name"])?.Value<string>()) == null || !(value is JValue))
                {
                    Log($"Ignoring invalid sink, missing name: {sink.ToString()}", ConsoleColor.Yellow);
                    continue;
                }
                if ((destination = (value = sinkObject["destination"])?.Value<string>()) == null || !(value is JValue))
                {
                    Log($"Ignoring invalid sink, missing destination: {sink.ToString()}", ConsoleColor.Yellow);
                    continue;
                }
                if ((filter = (value = sinkObject["filter"])?.Value<string>()) == null || !(value is JValue))
                {
                    Log($"Ignoring invalid sink, missing filter: {sink.ToString()}", ConsoleColor.Yellow);
                    continue;
                }

                sinkList.Add(new Sink()
                {
                    NodeId = nodeId,
                    NodeName = nodeName,
                    Name = name,
                    Destination = destination,
                    Filter = filter
                });
            }

            return sinkList;
        }

        static bool TryParseJObject(string json, out JObject jobject)
        {
            try
            {
                jobject = JObject.Parse(json);
            }
            catch
            {
                jobject = new JObject();
                return false;
            }

            return true;
        }

        static bool TryParseJArray(string json, out JArray jarray)
        {
            try
            {
                jarray = JArray.Parse(json);
            }
            catch
            {
                jarray = new JArray();
                return false;
            }

            return true;
        }

        static void Log(string message, ConsoleColor color)
        {
            var oldcolor = Console.ForegroundColor;
            Console.ForegroundColor = color;
            Console.WriteLine(message);
            Console.ForegroundColor = oldcolor;
        }
    }
}
