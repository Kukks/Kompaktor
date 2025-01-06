using System.Collections.Concurrent;
using System.Text;
using Kompaktor.Models;
using Microsoft.Extensions.Logging;
using WabiSabi.Crypto.Randomness;

namespace Kompaktor.Utils;

public static class DependencyGraph2
{
    public class Output
    {
        public Guid Id { get; } = Guid.NewGuid();
        public long Amount { get; }

        public Output(long amount)
        {
            Amount = amount;
        }

        // Override Equals and GetHashCode for correct comparison
        public override bool Equals(object obj)
        {
            return obj is Output output && Id.Equals(output.Id);
        }

        public override int GetHashCode()
        {
            return Id.GetHashCode();
        }

        public override string ToString()
        {
            return $"{Amount} ({Id})";
        }
    }

    internal class RootNode : Node
    {
        public RootNode(ILogger logger, Output[] ins) : base(logger, null, null, ins, null,
            new Dictionary<Node, Output[]>())
        {
        }
    }
    
    public class Node
    {
        private HashSet<Node> GetAllNodes()
        {
            var allNodes = new HashSet<Node>();
            var queue = new Queue<Node>();
            queue.Enqueue(this);

            while (queue.Count > 0)
            {
                var node = queue.Dequeue();
                if (allNodes.Add(node))
                {
                    foreach (var child in node.Children.Keys)
                    {
                        queue.Enqueue(child);
                    }
                }
            }

            return allNodes;
        }
        /// <summary>
        ///  Recursive reissuance function that schedules all actions in the graph in the correct order, with a randomized delay.
        /// </summary>
        /// <param name="expiry">All actions must be scheduled BEFORE this</param>
        /// <param name="random">random number gen</param>
        /// <param name="logger">logger</param>
        /// <param name="issuanceTask">the action that will execute</param>
        /// <param name="cancellationToken">cancels</param>
       public async Task Reissue(
    DateTimeOffset expiry,
    WasabiRandom random,
    ILogger logger,
    Func<Node, CancellationToken, Task> issuanceTask,
    CancellationToken cancellationToken)
{
    var ctsOnError = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    var token = ctsOnError.Token;

    try
    {
        // Build a list of all nodes and their dependencies
        var allNodes = GetAllNodes(); // Implement a method to get all nodes in the graph
        var dependencyCounts = new ConcurrentDictionary<Node, int>();
        var nodeChildren = new ConcurrentDictionary<Node, List<Node>>();

        // Initialize dependencies and child relationships
        foreach (var node in allNodes)
        {
            int dependencyCount = node.Parents.Keys.Count(parent => parent != node && !(parent is RootNode));
            dependencyCounts[node] = dependencyCount;

            foreach (var parent in node.Parents.Keys)
            {
                if (parent == node || parent is RootNode)
                {
                    continue;
                }

                nodeChildren.AddOrUpdate(parent, new List<Node> { node }, (key, list) =>
                {
                    list.Add(node);
                    return list;
                });
            }
        }

        // Initialize a concurrent queue for ready nodes
        var readyNodes = new ConcurrentQueue<Node>();

        // Enqueue nodes with zero dependencies
        foreach (var node in allNodes)
        {
            if (dependencyCounts[node] == 0)
            {
                readyNodes.Enqueue(node);
            }
        }

        // List to keep track of running tasks
        var runningTasks = new ConcurrentBag<Task>();

        // Semaphore to control access to the queue
        var semaphore = new SemaphoreSlim(1, 1);

        // Function to process nodes
        async Task ProcessNodesAsync()
        {
            while (!token.IsCancellationRequested)
            {
                if (!readyNodes.TryDequeue(out var currentNode))
                {
                    // No nodes to process currently
                    // Check if all tasks are done
                    if (runningTasks.IsEmpty)
                        break;

                    // Wait briefly before retrying
                    await Task.Delay(100, token).ConfigureAwait(false);
                    continue;
                }

                // Start processing the node
                var task = Task.Run(async () =>
                {
                    try
                    {
                        // Recalculate the available time
                        var remainingTime = expiry - DateTimeOffset.UtcNow;

                        if (remainingTime <= TimeSpan.Zero)
                        {
                            remainingTime = TimeSpan.Zero;
                        }

                        // Estimate the total time needed for all descendants
                        int descendantCount = currentNode.GetMaxDepth();
                        // Estimate minimum time per node (processing time + average delay)
                        TimeSpan minTimePerNode = TimeSpan.FromSeconds(5); // Adjust based on your system
                        var minDescendantTime = TimeSpan.FromTicks(descendantCount * minTimePerNode.Ticks);

                        // Calculate the maximum allowed delay for this node
                        var maxDelay = remainingTime - minDescendantTime;
                        if (maxDelay <= TimeSpan.Zero)
                        {
                            maxDelay = TimeSpan.Zero;
                        }

                        if (!(currentNode is RootNode))
                        {
                            // Generate a random delay within the allowed range
                            var delayMilliseconds = 0; //random.GetInt(0, Math.Max(1, (int) maxDelay.TotalMilliseconds));

                            // Log the scheduling
                            // logger.LogInformation($"Scheduling node {currentNode.Id} after {delayMilliseconds} ms delay.");
                            // Introduce the randomized delay
                            await Task.Delay(delayMilliseconds, token).ConfigureAwait(false);

                            // logger.LogInformation($"Node {currentNode.Id} running");
                            // Execute the issuance task
                            await issuanceTask(currentNode, token).ConfigureAwait(false);

                            // logger.LogInformation($"Node {currentNode.Id} completed");
                        }

                        // Decrement dependency counts of child nodes
                        if (nodeChildren.TryGetValue(currentNode, out var children))
                        {
                            foreach (var child in children)
                            {
                                if (dependencyCounts.AddOrUpdate(child, 0, (key, count) => count - 1) == 0)
                                {
                                    readyNodes.Enqueue(child);
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        // Log the error and cancel all tasks
                        logger.LogException( $"Error executing issuance task for node {currentNode.Id}. Cancelling all tasks.",ex);
                        await ctsOnError.CancelAsync();
                        throw;
                    }
                }, token);

                runningTasks.Add(task);

                // Remove completed tasks from the bag to prevent memory leaks
                _ = task.ContinueWith(t => runningTasks.TryTake(out _), TaskContinuationOptions.OnlyOnRanToCompletion);
            }
        }

        // Start processing nodes
        var processingTask = ProcessNodesAsync();

        // Wait for processing to complete
        await processingTask.ConfigureAwait(false);

        // Wait for all running tasks to complete
        await Task.WhenAll(runningTasks.ToArray()).ConfigureAwait(false);
    }
    catch (OperationCanceledException)
    {
        logger.LogWarning("Reissue operation was canceled.");
        throw;
    }
    catch (Exception ex)
    {
        logger.LogException( "An unexpected error occurred during Reissue.", ex);
        throw;
    }
    finally
    {
        ctsOnError.Dispose();
    }
}



        public int GetMaxDepth(Node? node = null)
        {
            node??=this;
            if (!node.Children.Any())
                return 0; // Leaf node has a depth of 1

            return 1 + node.Children.Keys.Max(GetMaxDepth);
        }  
        public int GetDepth(Node node )
        {
            return  (node.Parents.Count == 0) ? 0 : 1 + node.Parents.Max(pair => GetDepth(pair.Key));
        }  
        public int CountDescendants(Node? node = null, HashSet<string>? visited = null)
        {
            node ??= this;
            visited ??= new HashSet<string>();

            int count = 0;

            foreach (var child in node.Children.Keys)
            {
                if (visited.Add(child.Id))
                {
                    // The child node hasn't been visited yet
                    count += 1; // Count this child
                    count += CountDescendants(child, visited); // Recurse into child
                }
                // If the child has already been visited, we skip it to prevent double-counting
            }

            return count;
        }

        public readonly string Id = Guid.NewGuid().ToString();

        public override int GetHashCode()
        {
            return Id.GetHashCode();
        }

        public override string ToString()
        {
            var insStr = string.Join(",", Ins.Select(o => o.Amount));
            var outsStr = string.Join(",", Outs.Select(o => $"{o.Amount}"));
            return $"{Id} I:{insStr} O:{outsStr} OR:{OutputRegistered?.Amount}";
        }


        public Node(ILogger logger, IntRange? input, IntRange? output, Output[] outs, Output outputRegistered,
            Dictionary<Node, Output[]> parents)
        {
            if (input is not null && !input.Contains(parents.SelectMany(pair => pair.Value).Count()))
            {
                throw new ArgumentOutOfRangeException(nameof(parents), "The number of inputs is out of range.");
            }

            if (output is not null && !output.Contains(outs.Length))
            {
                throw new ArgumentOutOfRangeException(nameof(outs), "The number of outputs is out of range.");
            }

            var parentSum = parents.SelectMany(pair => pair.Value).Sum(o => o.Amount);
            var outputsSum = outs.Sum(o => o.Amount) + (outputRegistered?.Amount ?? 0);

            if (this is not RootNode && parentSum != outputsSum)
            {
                throw new InvalidOperationException("The sum of the inputs must be equal to the sum of the outputs.");
            }

            Outs = outs.ToList();
            OutputRegistered = outputRegistered;
            Parents = parents;
            Children = new Dictionary<Node, Output[]>();
            // logger.LogInformation(
            //     $"Created {(outputRegistered is null ? "output registration" : "reissuance")} {this}");
        }

        public Dictionary<Node, Output[]> Parents { get; set; }
        public Dictionary<Node, Output[]> Children { get; set; }

        public void AddChild(Node child, Output[] outputs)
        {
            var remainingOuts = RemainingOuts.ToList();
            if (outputs.Any(output => !remainingOuts.Remove(output)))
            {
                throw new InvalidOperationException("The output is already registered.");
            }

            Children.Add(child, outputs);
            return;
        }

        public Output[] RemainingOuts
        {
            get
            {
                var outs = Outs.ToList();
                foreach (var child in Children)
                {
                    foreach (var output in child.Value)
                    {
                        outs.Remove(output);
                    }
                }

                return outs.ToArray();
            }
        }

        public (Node, Output)[] NestedRemainingOuts(bool skipOutputRegistrations)
        {
            IEnumerable<(Node, Output)> currentOutputs = Enumerable.Empty<(Node, Output)>();

            if (OutputRegistered is null || !skipOutputRegistrations)
            {
                currentOutputs = RemainingOuts.Select(x => (this, x));
            }

            var childOutputs = Children.SelectMany(x => x.Key.NestedRemainingOuts(skipOutputRegistrations));

            return currentOutputs.Concat(childOutputs).DistinctBy(tuple => tuple.Item2.Id)
                .OrderByDescending(tuple => tuple.Item2.Amount)
                .ThenBy(tuple => tuple.Item1.GetDepth(tuple.Item1)).ToArray();
        }

        public Output[] NestedOutputsRegistered =>
            (OutputRegistered is null ? Array.Empty<Output>() : new[] {OutputRegistered})
            .Concat(Children.SelectMany(x => x.Key.NestedOutputsRegistered)).DistinctBy(output => output.Id).ToArray();

        public List<Output> Ins => Parents.SelectMany(x => x.Value).ToList();
        public List<Output> Outs { get; set; }
        public Output? OutputRegistered { get; set; }

        
        public string GenerateAscii(long[] targets)
        {
            var sb = new StringBuilder();
            var visitedNodes = new HashSet<string>();
            var targetValues = targets.ToList();


            void TraverseNode(Node node, string indent, bool isLast)
            {
                var nodeId = node.Id;

                if (visitedNodes.Contains(nodeId))
                {
                    sb.AppendLine($"{indent}└── [Node {nodeId}] (Already Visited)");
                    return;
                }

                visitedNodes.Add(nodeId);

                var label = node.OutputRegistered != null ? $"Output Registration ({node.OutputRegistered.Amount})" :
                    node.Parents.Any() ? "Reissuance" : "Root";

                var nodeLine = $"{indent}{(isLast ? "└──" : "├──")} [Node {nodeId}] {label}";
                sb.AppendLine(nodeLine);

                indent += isLast ? "    " : "│   ";

                // Output the outputs of this node
                for (int i = 0; i < node.Outs.Count; i++)
                {
                    var output = node.Outs[i];
                    var outputId = output.Id;
                    var isOutputLast = i == node.Outs.Count - 1 && !node.Children.Any();

                    var outputLine = $"{indent}{(isOutputLast ? "└──" : "├──")} [Output {outputId}] Amount: {output.Amount}";

                    // Check if this output is registered to a target
                    if (node.OutputRegistered != null && node.OutputRegistered.Id == outputId)
                    {
                        targetValues.Remove(output.Amount); // Remove to avoid duplicate registrations
                    }

                    sb.AppendLine(outputLine);
                }

                // Traverse child nodes
                for (int i = 0; i < node.Children.Count; i++)
                {
                    var childPair = node.Children.ElementAt(i);
                    var childNode = childPair.Key;
                    var isChildLast = i == node.Children.Count - 1;

                    // Outputs leading to the child
                    foreach (var output in childPair.Value)
                    {
                        var outputLine = $"{indent}{(isChildLast ? "└──" : "├──")} [Output {output.Id}] Amount: {output.Amount}";
                        sb.AppendLine(outputLine);
                    }

                    TraverseNode(childNode, indent, isChildLast);
                }
            }

            // Start traversal from the root node
            TraverseNode(this, "", true);

            return sb.ToString();
        }

        
        public string GenerateGraphviz(long[] targets)
        {
            var sb = new StringBuilder();
            sb.AppendLine("digraph ReissuanceGraph {");
            sb.AppendLine("    node [shape=box];");

            // Track visited nodes to avoid duplicates
            // var visitedNodes = new HashSet<Node>();
            var visitedIO = new HashSet<string>(); // To track input/output/target items
            var lines = new HashSet<string>();
            var targetGraphItemsAvailable = new Dictionary<string, long>();

            // Add all targets as graph items
            for (var index = 0; index < targets.Length; index++)
            {
                var target = targets[index];
                var targetId = $"Target_{index}";
                targetGraphItemsAvailable.Add(targetId, target);
                if (visitedIO.Add(targetId))
                {
                    lines.Add($"    \"{targetId}\" [label=\"Target: {target}\", shape=circle];");
                }
            }


            // Recursive function to traverse the DAG and generate Graphviz output
            void TraverseGraph(Node node, bool first)
            {
                var nodeId = $"Node_{node.Id}";
                if (visitedIO.Add(nodeId))
                {
                    var label = first
                        ? "Root"
                        : node.OutputRegistered is null
                            ? $"Reissuance"
                            : $"Output Registration";

                    // Node representation with just a node identifier

                    lines.Add($"    \"{nodeId}\" [label=\"{label}\", shape=box];");
                }


                // If the node has an output registered, link it to the relevant target
                if (node.OutputRegistered != null)
                {
                    var registeredOutputAmount = node.OutputRegistered.Amount;
                    var targetEntry = targetGraphItemsAvailable.FirstOrDefault(x => x.Value == registeredOutputAmount);
                    if (targetEntry.Key != null && visitedIO.Contains(targetEntry.Key))
                    {
                        lines.Add($"    \"{nodeId}\" -> \"{targetEntry.Key}\" [style=dotted, label=\"Registered\"];");
                        targetGraphItemsAvailable.Remove(targetEntry.Key);
                    }
                }

                foreach (var @out in node.Outs)
                {
                    var outId = $"Out_{@out.Id}";
                    if (visitedIO.Add(outId))
                    {
                        lines.Add($"    \"{outId}\" [label=\"Output: {@out.Amount}\", shape=ellipse];");
                    }

                    lines.Add($"    \"{nodeId}\" -> \"{outId}\";");
                }

                // Link to child nodes and recursively traverse the graph
                foreach (var child in node.Children)
                {
                    var childNodeId = $"Node_{child.Key.Id}";

                    foreach (var outv in child.Value)
                    {
                        var id = $"Out_{outv.Id}";

                        lines.Add($"    \"{id}\" -> \"{childNodeId}\";");
                        lines.Add($"    \"{nodeId}\" -> \"{id}\";");
                    }

                    TraverseGraph(child.Key, false); // Traverse the child node
                }

                if (!first)
                {
                    foreach (var parent in node.Parents)
                    {
                        var parentNodeId = $"Node_{parent.Key.Id}";

                        foreach (var outv in parent.Value)
                        {
                            var id = $"Out_{outv.Id}";

                            lines.Add($"    \"{parentNodeId}\" -> \"{id}\";");
                            lines.Add($"    \"{id}\" -> \"{nodeId}\";");
                        }
                    }
                }
            }

            // Start the traversal from the root node
            TraverseGraph(this, true);

            foreach (var line in lines)
            {
                sb.AppendLine(line);
            }

            sb.AppendLine("}");
            return sb.ToString();
        }
    }

    public static Node Compute(ILogger logger, long[] ins, long[] outs, IntRange input, IntRange output)
    {
        // Convert ins and outs to Output objects
        var insOutputs = ins.Select(amount => new Output(amount)).ToArray();
        var outsOutputs = outs.Select(amount => new Output(amount)).ToArray();
        

        var rootNode = new RootNode(logger, insOutputs);
        

        
        if (ins.Sum() < outs.Sum())
        {
            throw new InvalidOperationException($"The sum of the inputs must be equal to the sum of the outputs. {ins.Sum()} != {outs.Sum()}");
        }
        logger.LogInformation($"Computing reissuance graph for inputs {string.Join(",", ins)} and outputs {string.Join(",", outs)}.");;

        Output[] RemainingOuts()
        {
            var o = outsOutputs.ToList();
            foreach (var or in rootNode.NestedOutputsRegistered)
            {
                o.Remove(or);
            }

            return o.ToArray();
        }

        var lastRoundAnyMatch = false;
        while (RemainingOuts() is {Length: > 0} remainingOuts)
        {
            var anyMatch = false;

            // Try to find exact matches first, these are direct output registrations
            foreach (var remaining in remainingOuts)
            {
                // Try to find a combination of inputs within the input range that matches the remaining output
                var smallerAmts = rootNode.NestedRemainingOuts(lastRoundAnyMatch)
                    .Where(x => x.Item2.Amount <= remaining.Amount)
                    
                    .ToArray();
                if(smallerAmts.Sum(x => x.Item2.Amount) < remaining.Amount)
                    continue;

                var match =  FindBestMatchThroughGreedy(smallerAmts, remaining.Amount, input, true);
                
                
                if (match is not null && match.Sum(x => x.Item2.Amount) == remaining.Amount)
                {
                    var extra0Outs = Enumerable.Range(0, output.Max).Select(_ => new Output(0)).ToArray();

                    var newNode = new Node(logger, input, output, extra0Outs, remaining,
                        match.GroupBy(x => x.Item1)
                            .ToDictionary(x => x.Key, x => x.Select(y => y.Item2).ToArray()));

                    anyMatch = true;
                    lastRoundAnyMatch = true;

                    foreach (var parent in newNode.Parents)
                    {
                        parent.Key.AddChild(newNode, parent.Value);
                    }
                }
            }

            if (anyMatch) continue;

            foreach (var remaining in remainingOuts)
            {
                // Try to find a combination of inputs within the input range that matches the remaining output
                var amts = rootNode.NestedRemainingOuts(lastRoundAnyMatch)
                    .OrderByDescending(tuple => tuple.Item2.Amount)
                    .ToArray();

                var match = FindBestMatchThroughGreedy(amts, remaining.Amount, new IntRange(input.Min, input.Max*50), false);

                if (match is not null && match.Sum(x => x.Item2.Amount) >= remaining.Amount)
                {
                    var isOutOfRange = !input.Contains(match.Length);
                    Node newNode;
                    if (isOutOfRange && match.Length > input.Max)
                    {
                        // We need to merge some of the inputs
                        var subsetInputs = match.Take(input.Max).ToArray();
                        var mergedAmount = subsetInputs.Sum(tuple => tuple.Item2.Amount);
                        var mergedOutput = new Output(mergedAmount);

                        var extra0Outs = Enumerable.Range(0, output.Max - 1).Select(_ => new Output(0)).ToArray();


                        newNode = new Node(logger, input, output,
                            extra0Outs
                                .Concat(new[] {mergedOutput}).ToArray(),
                            null,
                            subsetInputs.GroupBy(x => x.Item1)
                                .ToDictionary(x => x.Key, x => x.Select(y => y.Item2).ToArray()));

                        foreach (var parent in newNode.Parents)
                        {
                            parent.Key.AddChild(newNode, parent.Value);
                        }
                    }
                    else
                    {
                        var totalMatchAmount = match.Sum(x => x.Item2.Amount);
                        var changeAmount = totalMatchAmount - remaining.Amount;
                        var changeOutput = new Output(changeAmount);
                        var remainingOutput = remaining;
                        var isOutputRegistration = changeAmount == 0 || !lastRoundAnyMatch;
                        Output[] outputs;

                        if (isOutputRegistration)
                        {
                            outputs = new[] {changeOutput};
                        }
                        else
                        {
                            outputs = new[] {changeOutput, new Output(remainingOutput.Amount)};
                        }

                        var extra0Outs = Enumerable.Range(0, output.Max - outputs.Length).Select(_ => new Output(0))
                            .ToArray();
                        outputs = outputs.Concat(extra0Outs).ToArray();

                        newNode = new Node(logger, input, output,
                            outputs,
                            isOutputRegistration ? remaining : null,
                            match.GroupBy(x => x.Item1)
                                .ToDictionary(x => x.Key, x => x.Select(y => y.Item2).ToArray()));

                        foreach (var parent in newNode.Parents)
                        {
                            parent.Key.AddChild(newNode, parent.Value);
                        }
                    }

                    anyMatch = true;
                    lastRoundAnyMatch = true;

                    break;
                }
            }

            lastRoundAnyMatch = false;
        }

        return rootNode;
    }
    
    private static (Node, Output)[]? FindBestMatchThroughGreedy(
        (Node, Output)[] amtsAvailable, long target, IntRange inRange, bool equalOnly)
    {
        // Sort the available amounts based on shallower depth and larger amounts
        var sortedAmts = amtsAvailable
            .OrderByDescending(tuple => tuple.Item2.Amount)       // Prefer larger amounts
            .ThenBy(tuple => tuple.Item1.GetDepth(tuple.Item1))
            .ToList();

        var selectedOutputs = new List<(Node, Output)>();
        long totalSum = 0;

        foreach (var (node, output) in sortedAmts)
        {
            if (selectedOutputs.Count >= inRange.Max)
            {
                break; // Reached the maximum number of inputs allowed
            }

            // Skip outputs that have already been selected
            if (selectedOutputs.Any(o => o.Item2.Id == output.Id))
            {
                continue;
            }

            selectedOutputs.Add((node, output));
            totalSum += output.Amount;

            if (equalOnly && totalSum == target && selectedOutputs.Count >= inRange.Min)
            {
                // Exact match found within input range
                return selectedOutputs.ToArray();
            }
            else if (!equalOnly && totalSum >= target && selectedOutputs.Count >= inRange.Min)
            {
                // Target met or exceeded within input range
                return selectedOutputs.ToArray();
            }
        }

        // If we didn't meet the target, or didn't have enough inputs, return null
        return null;
    }

    
    private static (Node, Output)[]? FindBestMatchThroughDynamicProgramming(
        (Node, Output)[] amtsAvailable, long target, IntRange inRange, bool equalOnly)
    {
        var sumToComb = new Dictionary<long, List<(List<int> Indices, int Count)>>();

        sumToComb[0] = new List<(List<int>, int)> { (new List<int>(), 0) };

        int n = amtsAvailable.Length;
        long[] amounts = amtsAvailable.Select(x => x.Item2.Amount).ToArray();

        foreach (var (amount, index) in amounts.Select((amt, idx) => (amt, idx)))
        {
            var newSums = new Dictionary<long, List<(List<int>, int)>>();

            foreach (var (sum, combList) in sumToComb)
            {
                long newSum = sum + amount;
                if (equalOnly && newSum > target)
                {
                    continue; // Skip sums larger than target if exact match is required
                }

                foreach (var (indices, count) in combList)
                {
                    if (count + 1 > inRange.Max)
                    {
                        continue; // Skip combinations exceeding max elements
                    }

                    var newIndices = new List<int>(indices) { index };
                    if (!newSums.ContainsKey(newSum))
                    {
                        newSums[newSum] = new List<(List<int>, int)>();
                    }
                    newSums[newSum].Add((newIndices, count + 1));
                }
            }

            foreach (var (newSum, combList) in newSums)
            {
                if (!sumToComb.ContainsKey(newSum))
                {
                    sumToComb[newSum] = combList;
                }
                else
                {
                    sumToComb[newSum].AddRange(combList);
                }
            }
        }

        // Find the best matching sum
        long bestSum = equalOnly ? target : sumToComb.Keys.Where(s => s >= target).Order().FirstOrDefault();

        if (!sumToComb.TryGetValue(bestSum, out var value))
        {
            return null; // No suitable combination found
        }

        // Find the combination with the smallest number of elements within the range
        var validCombinations = value
            .Where(c => c.Count >= inRange.Min && c.Count <= inRange.Max)
            .ToList();

        if (!validCombinations.Any())
        {
            return null; // No valid combinations within the element count constraints
        }

        var bestCombination = validCombinations.OrderBy(c => c.Count).First();

        // Construct the result
        var result = bestCombination.Indices.Select(index => amtsAvailable[index]).ToList();

        // Prefer shallower outputs
        result = PreferShallowerOutputs(result, amtsAvailable);

        return result.ToArray();
    }


    private static List<(Node, Output)> PreferShallowerOutputs(
        List<(Node, Output)> currentMatch,
        (Node, Output)[] amtsAvailable)
    {
        var newMatch = currentMatch.ToList();

        foreach (var matchItem in currentMatch)
        {
            // Try to find a smaller depth but equivalent amount match
            if (matchItem.Item1.OutputRegistered is null)
            {
                continue;
            }
            var found =
                amtsAvailable
                    .Where(tuple => tuple.Item2.Amount == matchItem.Item2.Amount)
                    .Where(tuple => !newMatch.Select(valueTuple => valueTuple.Item2.Id).Contains(tuple.Item2.Id))
                    .OrderBy(x => x.Item1.GetDepth(x.Item1))
                    .ThenBy(tuple => tuple.Item1.OutputRegistered is null)
                    .FirstOrDefault();

            if (found.Item1 is not null)
            {
                newMatch.Remove(matchItem);
                newMatch.Add(found);
            }
        }
        return newMatch;
    }
    
//     private static (Node, Output)[]? FindBestMatch((Node, Output)[] amtsAvailable, long target, Range inRange, bool equalOnly)
// {
//     int n = amtsAvailable.Length;
//     int minElements = inRange.Min;
//     int maxElements = inRange.Max;
//
//     // Total sum of all amounts
//     long totalSum = amtsAvailable.Sum(x => x.Item2.Amount);
//
//     // Early exit if target is greater than total sum
//     if (totalSum < target)
//     {
//         return null;
//     }
//     // Early exit if target is greater than total sum
//     if (totalSum == target &&  inRange.Contains(amtsAvailable.Length) )
//     {
//         return amtsAvailable;
//     }
//     else if(totalSum == target)
//     {
//         while (!inRange.Contains(amtsAvailable.Length) && amtsAvailable.FirstOrDefault(tuple => tuple.Item2.Amount == 0) is { } zero)
//         {
//             amtsAvailable = amtsAvailable.Except(new[] {zero}).ToArray();
//         }
//
//         return inRange.Contains(amtsAvailable.Length) ? amtsAvailable : null;
//     }
//
//     // Initialize DP table: dp[i][s][k] = true if sum s is achievable with k elements from first i elements
//     var dp = new bool[n + 1, totalSum + 1, maxElements + 1];
//
//     // Base case: sum 0 is achievable with 0 elements
//     dp[0, 0, 0] = true;
//
//     // Fill DP table
//     for (int i = 1; i <= n; i++)
//     {
//         long currentAmount = amtsAvailable[i - 1].Item2.Amount;
//
//         for (long s = 0; s <= totalSum; s++)
//         {
//             for (int k = 0; k <= maxElements; k++)
//             {
//                 // Without current element
//                 if (dp[i - 1, s, k])
//                 {
//                     dp[i, s, k] = true;
//                 }
//
//                 // With current element
//                 if (k > 0 && s >= currentAmount && dp[i - 1, s - currentAmount, k - 1])
//                 {
//                     dp[i, s, k] = true;
//                 }
//             }
//         }
//     }
//
//     // Search for the best sum
//     long bestSum = equalOnly ? -1 : long.MaxValue;
//     int bestK = -1;
//
//     for (int k = minElements; k <= maxElements; k++)
//     {
//         for (long s = target; s <= totalSum; s++)
//         {
//             if (dp[n, s, k])
//             {
//                 if (equalOnly && s == target)
//                 {
//                     bestSum = s;
//                     bestK = k;
//                     goto Reconstruct; // Exact match found
//                 }
//                 else if (!equalOnly && s >= target && s < bestSum)
//                 {
//                     bestSum = s;
//                     bestK = k;
//                 }
//             }
//         }
//     }
//
//     if (bestSum == -1 || bestSum == long.MaxValue)
//     {
//         // No suitable sum found
//         return null;
//     }
//
// Reconstruct:
//     // Reconstruct the subset
//     var result = new List<(Node, Output)>();
//     int iIndex = n;
//     long sSum = bestSum;
//     int kCount = bestK;
//
//     while (iIndex > 0 && sSum >= 0 && kCount >= 0)
//     {
//         if (kCount > 0 && sSum >= amtsAvailable[iIndex - 1].Item2.Amount &&
//             dp[iIndex - 1, sSum - amtsAvailable[iIndex - 1].Item2.Amount, kCount - 1])
//         {
//             // Include this element
//             result.Add(amtsAvailable[iIndex - 1]);
//             sSum -= amtsAvailable[iIndex - 1].Item2.Amount;
//             kCount--;
//         }
//         iIndex--;
//     }
//
//     // Reverse the result to maintain original order
//     result.Reverse();
//
//     var newMatch = result.ToList();
//
//     foreach (var matchItem in result)
//     {
//         //try to find a smaller depth but equivalent amt match
//         if (matchItem.Item1.OutputRegistered is null)
//         {
//             continue;
//         }
//         var found =  
//             amtsAvailable
//                 .Where(tuple => tuple.Item2.Amount == matchItem.Item2.Amount)
//                 .Where(tuple => !newMatch.Select(valueTuple => valueTuple.Item2.Id).Contains(tuple.Item2.Id)).ToArray()
//                 .OrderBy(x => x.Item1.GetDepth(x.Item1))
//                 .ThenBy(tuple => tuple.Item1.OutputRegistered is null)
//                 .FirstOrDefault(x => x.Item2.Amount == matchItem.Item2.Amount);
//                  
//         if (found.Item1 is not null)
//         {   newMatch.Remove(matchItem);
//             newMatch.Add(found);
//         }
//                     
//     }
//     return newMatch.ToArray();
// }

    //
    // private static (Node, Output)[]? FindBestMatch((Node, Output)[] amtsAvailable, long target, Range inRange,
    //     bool equalOnly)
    // {
    //   
    //     (Node, Output)[]? bestMatch = null;
    //     long bestSum = equalOnly ? 0 : long.MaxValue;
    //
    //     // Iterate over the specified range of combination sizes
    //     for (int k = inRange.Min; k <= inRange.Max; k++)
    //     {
    //         // Generate all combinations of k elements
    //         var combinations = GetCombinations(amtsAvailable, k);
    //
    //         foreach (var combination in combinations)
    //         {
    //             long sum = combination.Sum(x => x.Item2.Amount);
    //
    //             if (equalOnly)
    //             {
    //                 if (sum == target)
    //                 {
    //                     // Found an exact match
    //                     return combination.ToArray();
    //                 }
    //             }
    //             else
    //             {
    //                 if (sum >= target && sum < bestSum)
    //                 {
    //                     // Found a better match
    //                     bestSum = sum;
    //                     bestMatch = combination.ToArray();
    //                 }
    //             }
    //         }
    //     }
    //
    //     return bestMatch;
    // }
    //
    // private static IEnumerable<IEnumerable<T>> GetCombinations<T>(IEnumerable<T> list, int length)
    // {
    //     if (length == 0)
    //     {
    //         return new[] {Array.Empty<T>()};
    //     }
    //
    //     return list.SelectMany((element, index) =>
    //         GetCombinations(list.Skip(index + 1), length - 1)
    //             .Select(combination => (new[] {element}).Concat(combination)));
    // }
}