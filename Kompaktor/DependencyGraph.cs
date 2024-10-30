// using System;
// using System.Collections.Generic;
// using System.Linq;
// using System.Text;
// using System.Text;
// using Kompaktor.Utils;
// using Microsoft.Extensions.Logging;
// using WabiSabi.Crypto.Randomness;
// using WalletWasabi.Crypto;
// using TaskScheduler = Kompaktor.Utils.TaskScheduler;
//
// namespace Kompaktor;
//
// public static class DependencyGraph
// {
//     public class Match
//     {
//         public List<long> Inputs { get; set; }
//         public long Target { get; set; }
//
//         public Match(int minIn, List<long> inputs, long target)
//         {
//             if (inputs.Count < minIn)
//             {
//                 throw new ArgumentException($"Number of inputs must be at least {minIn}");
//             }
//
//             Inputs = inputs;
//             Target = target;
//         }
//
//         public override string ToString()
//         {
//             string inputsStr = string.Join(", ", Inputs);
//             return $"Inputs: [{inputsStr}] -> Target: {Target}";
//         }
//     }
//
//     public class Reissuance
//     {
//         public int Depth { get; }
//         public List<long> Inputs { get; set; }
//         public List<long> Outputs { get; set; }
//
//         public Reissuance(int depth, int minIn, int maxIn, int minOut, int maxOut, List<long> inputs, List<long> outputs)
//         {
//             Depth = depth;
//             if (inputs.Count < minIn || inputs.Count > maxIn)
//             {
//                 throw new ArgumentException($"Number of inputs must be between {minIn} and {maxIn} (got {inputs.Count})");
//             }
//
//             if (outputs.Count < minOut || outputs.Count > maxOut)
//             {
//                 throw new ArgumentException($"Number of outputs must be between {minOut} and {maxOut} (got {outputs.Count})");
//             }
//
//             if (inputs.Sum() != outputs.Sum())
//                 throw new ArgumentException(
//                     $"Sum of inputs must be equal to sum of outputs (in:{inputs.Sum()} out:{outputs.Sum()}) (diff: {inputs.Sum() - outputs.Sum()})");
//
//             Inputs = inputs;
//             Outputs = outputs;
//         }
//
//         public override string ToString()
//         {
//             string inputsStr = string.Join(", ", Inputs);
//             string outputsStr = string.Join(", ", Outputs);
//             return $"Depth:{Depth}: Inputs: [{inputsStr}] -> Outputs: [{outputsStr}]";
//         }
//     }
//
//     public class Result
//     {
//         public int MinIn { get; }
//         public int MaxIn { get; }
//         public int MinOut { get; }
//         public int MaxOut { get; }
//         public List<long> InitialCredentials { get; }
//         public List<long> Targets { get; }
//         public Reissuance[] Reissuances { get; }
//         public Match[] Matches { get; }
//         public List<long> RemainderCredentials { get; }
//
//         public Result(
//             int minIn, int maxIn, int minOut, int maxOut,
//             List<long> initialCredentials,
//             List<long> targets,
//             Reissuance[] reissuances,
//             Match[] matches,
//             List<long> remainderCredentials)
//         {
//             MinIn = minIn;
//             MaxIn = maxIn;
//             MinOut = minOut;
//             MaxOut = maxOut;
//             InitialCredentials = initialCredentials;
//             Targets = targets;
//             Reissuances = reissuances;
//             Matches = matches;
//             RemainderCredentials = remainderCredentials;
//         }
//
//         public override string ToString()
//         {
//             var str = new StringBuilder();
//             str.AppendLine(
//                 $"Initial Credentials: [{string.Join(", ", InitialCredentials)}] => Target Values: [{string.Join(", ", Targets)}]. minIn={MinIn}, maxIn={MaxIn}, minOut={MinOut}, maxOut={MaxOut}");
//             str.AppendLine(
//                 $"Reissuances: {Reissuances.Length} {string.Join(", ", Reissuances.Select(action => action.ToString()))}");
//             str.AppendLine(
//                 $"Matches: {string.Join(", ", Matches.Select(match => match.ToString()))}");
//             str.AppendLine(
//                 $"Remainder Credentials: [{string.Join(", ", RemainderCredentials)}]");
//             return str.ToString();
//         }
//
//         public string GenerateGraphviz()
//         {
//             var result = this;
//             var sb = new StringBuilder();
//             sb.AppendLine("digraph ReissuanceGraph {");
//             sb.AppendLine("    node [shape=box];");
//
//             // Add nodes for initial credentials
//             var available = new Dictionary<string, long>();
//             int ix;
//             for (ix = 0; ix < result.InitialCredentials.Count; ix++)
//             {
//                 sb.AppendLine($"    C{ix} [label=\"C{result.InitialCredentials[ix]}\", shape=ellipse];");
//                 available[$"C{ix}"] = result.InitialCredentials[ix];
//             }
//
//
//             // Add nodes for reissuances
//             for (int i = 0; i < result.Reissuances.Length; i++)
//             {
//                 var reissuance = result.Reissuances[i];
//                 sb.AppendLine(
//                     $"    R{i} [label=\"Reissuance {i} (Depth {reissuance.Depth})\\nInputs: [{string.Join(", ", reissuance.Inputs)}]\\nOutputs: [{string.Join(", ", reissuance.Outputs)}]\"];");
//                 foreach (var input in reissuance.Inputs)
//                 {
//                     var keyToUse = available.First(x => x.Value == input).Key;
//                     if (available.Remove(keyToUse))
//                         sb.AppendLine($"    {keyToUse} -> R{i};");
//                 }
//
//                 foreach (var output in reissuance.Outputs)
//                 {
//                     sb.AppendLine($"    C{ix} [label=\"C{output}\", shape=ellipse];");
//                     available[$"C{ix}"] = output;
//                     sb.AppendLine($"    R{i} -> C{ix};");
//                     ix++;
//                 }
//             }
//
//             // Add nodes for matches
//             for (var i = 0; i < result.Matches.Length; i++)
//             {
//                 var match = result.Matches[i];
//                 sb.AppendLine(
//                     $"    M{i} [label=\"Match {i}\\nInputs: [{string.Join(", ", match.Inputs)}]\\nTarget: {match.Target}\", shape=ellipse];");
//                 foreach (var input in match.Inputs)
//                 {
//                     var keyToUse = available.First(x => x.Value == input).Key;
//                     if (available.Remove(keyToUse))
//                         sb.AppendLine($"    {keyToUse} -> M{i};");
//                 }
//             }
//
//             sb.AppendLine("}");
//             return sb.ToString();
//         }
//
//         public async Task Reissue(DateTimeOffset expiry, WasabiRandom random, ILogger logger, Func<long[], long[], CancellationToken, Task> issuanceTask, CancellationToken cancellationToken)
//         {
//             if(Reissuances.Length == 0)
//                 return;
//             using CancellationTokenSource ctsOnError =  CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
//
//             var depths = Reissuances.GroupBy(reissuance => reissuance.Depth).OrderBy(grouping => grouping.Key);
//             
//             var timespan = TimeSpan.FromMilliseconds(Math.Max(0, ( expiry - DateTimeOffset.UtcNow ).TotalMilliseconds) )/depths.Count();
//             foreach (var g in depths)
//
//             {
//                 var tasks = g.Select<Reissuance, Func<Task>>(reissuance => () => issuanceTask(reissuance.Inputs.ToArray(), reissuance.Outputs.ToArray(), ctsOnError.Token)
//                     .OnFailureCancel(ctsOnError)).ToArray();
//
//
//                 await TaskScheduler.Schedule("Reissuance",tasks, DateTimeOffset.UtcNow+timespan, random, cancellationToken,
//                     logger);
//             }
//         }
//         
//         
//         
//     }
//     
//     
//
//     public static DependencyGraph.Result GenerateReissuanceActions(
//         List<long> initialCredentials, List<long> targetValues, int minIn, int maxIn, int minOut, int maxOut)
//     {
//         var targetValuesCopy = targetValues.ToList();
//         if (initialCredentials.Sum() != targetValues.Sum())
//         {
//             throw new ArgumentException(
//                 $"Sum of initial credentials must be equal to sum of target values (in:{initialCredentials.Sum()} out:{targetValues.Sum()})");
//         }
//
//         List<long> currentCredentials = new List<long>(initialCredentials);
//         List<DependencyGraph.Reissuance> actions = new List<DependencyGraph.Reissuance>();
//         // List<long> remainderCredentials = new List<long>();
//
//         // Track matched targets and reserved credentials
//         List<long> reservedCredentials = new List<long>();
//         int actionId = 0; // Unique ID counter for each reissuance action
//
//         List<Match> matches = new List<Match>();
//
//         List<long> AvailableCredentials(List<long>? additionallyReserved = null)
//         {
//             var result = currentCredentials.ToList();
//             foreach (var reserved in reservedCredentials)
//             {
//                 result.Remove(reserved);
//             }
//
//             if (additionallyReserved != null)
//             {
//                 foreach (var reserved in additionallyReserved)
//                 {
//                     result.Remove(reserved);
//                 }
//             }
//
//             return result;
//         }
//
//
//         // Reissuances to match remaining targets
//         var currDepth = -1;
//         var mergeTime = false;
//         while (targetValues.Any())
//         {
//             currDepth++;
//             // Match without reissuance
//             foreach (var target in targetValues.ToList())
//             {
//                 if (AvailableCredentials().Any(cred => cred == target))
//                 {
//                     long matched = AvailableCredentials().First(cred => cred == target);
//                     if (minIn > 1)
//                     {
//                         //try find enough 0s to match the minIn
//                         var zeros = AvailableCredentials().Where(cred => cred == 0).Take(minIn - 1).ToList();
//                         if (zeros.Count == minIn - 1)
//                         {
//                             reservedCredentials.Add(matched);
//                             reservedCredentials.AddRange(zeros);
//                             matches.Add(new Match(minIn, zeros.Concat(new List<long> {matched}).ToList(), target));
//                             targetValues.Remove(target);
//                         }
//                     }
//                 }
//             }
//
//             var anySolved = targetValues.Count;
//             var newActions = new List<DependencyGraph.Reissuance>();
//             var newMatches = new List<Match>();
//
//             foreach (var target in targetValues.ToList())
//             {
//                 List<long> selected = new List<long>();
//
//
//                 // Select between minIn and maxIn credentials that sum up as close to the target as possible
//                 foreach (var credential in AvailableCredentials().Order())
//                 {
//                     if (selected.Count >= maxIn)
//                     {
//                         //replace the smallest credential with the current one
//                         if (selected.Min() < credential)
//                         {
//                             selected.Remove(selected.Min());
//                             selected.Add(credential);
//                         }
//                     }
//                     else if (selected.Count < maxIn && selected.Sum() < target)
//                     {
//                         selected.Add(credential);
//                     }
//                     else if (selected.Count < minIn)
//                     {
//                         selected.Add(credential);
//                     }
//
//
//                     if (selected.Count >= minIn && selected.Count <= maxIn && selected.Sum() >= target)
//                         break;
//                 }
//
//                 // check if the ins can compose the target exactly
//                 if (selected.Sum() == target)
//                 {
//                     //try find enough 0s to match the minIn
//                     var zeros = AvailableCredentials().Where(cred => cred == 0).Take(minIn - selected.Count).ToList();
//                     if (zeros.Count == minIn - selected.Count)
//                     {
//                         selected.AddRange(zeros);
//                     }
//
//                     if (minIn - selected.Count > 0)
//                         continue;
//
//                     if (AvailableCredentials().Count - selected.Count < minOut && targetValues.Count > 1)
//                     {
//                         //we need to create some 0 amout credentials to fulfill the minOut
//                         mergeTime = true;
//                     }
//                     else
//                     {
//                         matches.Add(new Match(minIn, selected, target));
//                         reservedCredentials.AddRange(selected);
//                         targetValues.Remove(target);
//                         break;
//                     }
//                 }
//
//                 if (selected.Sum() > target)
//                 {
//                     long remainder = selected.Sum() - target;
//
//                     // Lists to track total outputs and outputs directly fulfilling the target
//                     List<long> outputs = new List<long>();
//                     List<long> targetOuts = new List<long>();
//
//                     targetOuts.Add(target);
//                     outputs.Add(target);
//                     if (remainder > 0)
//                     {
//                         outputs.Add(remainder);
//                     }
//
//                     var generated0s = 0;
//                     while (outputs.Count < maxOut)
//                     {
//                         outputs.Add(0);
//                         generated0s++;
//                         
//                     }
//                     while(targetOuts.Count < minIn && generated0s > 0)
//                     {
//                         targetOuts.Add(0);
//                         generated0s--;
//                     }
//                     var zeros = AvailableCredentials().Where(cred => cred == 0).Take(minIn - selected.Count).ToList();
//                     if (zeros.Count == minIn - selected.Count)
//                     {
//                         selected.AddRange(zeros);
//                     }else if(targetOuts.Count < minIn)
//                     {
//                         mergeTime = true;
//                     }
//                     else
//                     {
//
//
//
//
//                         // Record the action and match
//                         newActions.Add(new DependencyGraph.Reissuance(currDepth, minIn, maxIn, minOut, maxOut,
//                             new List<long>(selected),
//                             outputs));
//                         newMatches.Add(new Match(minIn, targetOuts, target));
//
//                         foreach (var cred in selected)
//                         {
//                             currentCredentials.Remove(cred);
//                         }
//
//                         continue;
//                     }
//
//                 }
//
//                 if (mergeTime && selected.Any())
//                 {
//                     //create as many 0s as needed
//                     var outputs = new List<long>() {selected.Sum()};
//                     while (outputs.Count < maxOut)
//                     {
//                      
//                         if (outputs.Count(l => l == 0)+1 == selected.Count(l => l == 0))
//                         {
//                             //grab any non 0 output and rreplace with 2 outputs totaling the same
//                             var nonZero = outputs.First(l => l != 0);
//                             outputs.Remove(nonZero);
//                             //ensure we do not lose any to division 
//                             if (nonZero % 2 == 0)
//                             {
//                                 outputs.Add(nonZero / 2);
//                                 outputs.Add(nonZero / 2);
//                             }
//                             else
//                             {
//                                 outputs.Add(nonZero / 2);
//                                 outputs.Add(nonZero / 2 + 1);
//                             }
//                         }
//                         else
//                         {
//                             outputs.Add(0);
//                         }
//                         
//                     }
//
//                     newActions.Add(new DependencyGraph.Reissuance(currDepth, minIn, maxIn, minOut, maxOut, [..selected], outputs));
//                     foreach (var cred in selected)
//                     {
//                         currentCredentials.Remove(cred);
//                     }
//                 }
//             }
//
//             newActions.ForEach(action =>
//             {
//                 actions.Add(action);
//                 currentCredentials.AddRange(action.Outputs);
//             });
//             newMatches.ForEach(match =>
//             {
//                 matches.Add(match);
//                 targetValues.Remove(match.Target);
//                 reservedCredentials.AddRange(match.Inputs);
//             });
//
//             if (anySolved == targetValues.Count)
//             {
//                 mergeTime = true;
//             }
//         }
//
//         return new DependencyGraph.Result(minIn, maxIn, minOut, maxOut, initialCredentials, targetValuesCopy, actions.ToArray(),
//             matches.ToArray(), AvailableCredentials());
//     }
//
//     //
//     // public static void Main(string[] args)
//     // {
//     //     // Test cases with varied complexity, including zero-value credentials
//     //     List<long> initialCredentials1 = new List<long> {8, 0, 5, 7, 0};
//     //     List<long> targetValues1 = new List<long> {10, 10};
//     //
//     //     Console.WriteLine("Test Case 1:");
//     //     var actions1 = GenerateReissuanceActions(initialCredentials1, targetValues1.ToList(), 1, 2, 1, 3);
//     //     Console.WriteLine(actions1.ToString());
//     //
//     //     Console.WriteLine("\nTest Case 2:");
//     //     List<long> initialCredentials2 = new List<long> {15, 0, 10, 0, 5, 0, 5, 0};
//     //     List<long> targetValues2 = new List<long> {20, 15};
//     //     var actions2 = GenerateReissuanceActions(initialCredentials2, targetValues2.ToList(), 2, 3, 2, 3);
//     //     Console.WriteLine(actions2.ToString());
//     //
//     //     Console.WriteLine("\nTest Case 3:");
//     //     List<long> initialCredentials3 = new List<long> {50, 0, 30, 0};
//     //     List<long> targetValues3 = new List<long> {40, 40};
//     //     var actions3 = GenerateReissuanceActions(initialCredentials3, targetValues3.ToList(), 1, 2, 1, 3);
//     //     Console.WriteLine(actions3.ToString());
//     //     
//     //     Console.WriteLine("\nTest Case 4:");
//     //     List<long> initialCredentials4 = new List<long> {7,7,7,7,7,7,7,7,7,5,3};
//     //     List<long> targetValues4 = new List<long> {20,17,3,1,10,10,10};
//     //     var actions4 = GenerateReissuanceActions(initialCredentials4, targetValues4.ToList(), 1, 4, 2, 2);
//     //     Console.WriteLine(actions4.ToString());
//     //     
//     //     
//     //     
//     //     Console.WriteLine("\nTest Case 5: Requires Multiple Depths");
//     //     // Initial credentials are smaller, requiring multiple merging steps to reach target values
//     //     List<long> initialCredentials5 = new List<long> {5, 5, 5, 5, 2, 2, 1, 1, 0, 0, 0, 0,5,5,5,5,5,4};
//     //     List<long> targetValues5 = new List<long> {20, 15, 10, 10};
//     //     var actions5 = GenerateReissuanceActions(initialCredentials5, targetValues5.ToList(), 2, 2, 2, 2);
//     //     Console.WriteLine(actions5.ToString());
//     //
//     //     Console.WriteLine(actions5.GenerateGraphviz());
//     // }
// }