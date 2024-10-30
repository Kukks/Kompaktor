// using System.Collections.Concurrent;
//
// namespace Kompaktor.Utils;
//
// public static class BagExtensions
// {
//     public static void Remove<T>(this ConcurrentBag<T> bag, T item)
//     {
//         
//         while (bag.Count > 0)
//         {
//             T result;
//             bag.z(out result);
//
//             if (result.Equals(item))
//             {
//                 break; 
//             }
//
//             bag.Add(result);
//         }
//
//     }
// }