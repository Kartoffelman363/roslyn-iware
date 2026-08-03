// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Generic;

#pragma warning disable RS0016 // Add public types and members to the declared API
namespace Microsoft.CodeAnalysis.CSharp.iWareSql
{
    public static class Helpers
    {
        public static void Merge<T>(List<T> list, List<T> incomingList, HashSet<T> listHashSet)
        {
            var incomingHashSet = new HashSet<T>(incomingList);

            //Remove all elements from list not present in incomingList
            list.RemoveAll(tr =>
            {
                var shouldRemove = !incomingList.Contains(tr);
                if (shouldRemove)
                {
                    listHashSet.Remove(tr);
                }
                return shouldRemove;
            });

            // Add all elements from incomingList that don't exist in list
            foreach (var incoming in incomingList)
            {
                if (!listHashSet.Contains(incoming))
                {
                    list.Add(incoming);
                    listHashSet.Add(incoming);
                }
            }
        }
    }
}
#pragma warning restore RS0016 // Add public types and members to the declared API
