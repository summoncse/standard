﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Microsoft.DotNet.Build.Tasks
{
    public partial class HandlePackageFileConflicts : Task
    {
        HashSet<ITaskItem> referenceConflicts = new HashSet<ITaskItem>();
        HashSet<ITaskItem> copyLocalConflicts = new HashSet<ITaskItem>();
        HashSet<ConflictItem> allConflicts = new HashSet<ConflictItem>();

        public ITaskItem[] References { get; set; }

        public ITaskItem[] ReferenceCopyLocalPaths { get; set; }

        public ITaskItem[] OtherRuntimeItems { get; set; }

        public ITaskItem[] PlatformManifests { get; set; }

        /// <summary>
        /// NuGet3 and later only.  In the case of a conflict with identical file version information a file from the most preferred package will be chosen.
        /// </summary>
        public string[] PreferredPackages { get; set; }

        [Output]
        public ITaskItem[] ReferencesWithoutConflicts { get; set; }

        [Output]
        public ITaskItem[] ReferenceCopyLocalPathsWithoutConflicts { get; set; }

        [Output]
        public ITaskItem[] Conflicts { get; set; }

        public override bool Execute()
        {
            var log = new MSBuildLog(Log);
            var packageRanks = new PackageRank(PreferredPackages);

            // resolve conflicts at compile time
            var referenceItems = GetConflictTaskItems(References, ConflictItemType.Reference).ToArray();

            var compileConflictScope = new ConflictResolver(packageRanks, log);

            compileConflictScope.ResolveConflicts(referenceItems,
                ci => ItemUtilities.GetReferenceFileName(ci.OriginalItem),
                HandleCompileConflict);

            // resolve conflicts that class in output
            var runtimeConflictScope = new ConflictResolver(packageRanks, log);

            runtimeConflictScope.ResolveConflicts(referenceItems,
                ci => ItemUtilities.GetReferenceTargetPath(ci.OriginalItem),
                HandleRuntimeConflict);

            var copyLocalItems = GetConflictTaskItems(ReferenceCopyLocalPaths, ConflictItemType.CopyLocal).ToArray();

            runtimeConflictScope.ResolveConflicts(copyLocalItems,
                ci => ItemUtilities.GetTargetPath(ci.OriginalItem),
                HandleRuntimeConflict);

            var otherRuntimeItems = GetConflictTaskItems(OtherRuntimeItems, ConflictItemType.Runtime).ToArray();

            runtimeConflictScope.ResolveConflicts(otherRuntimeItems,
                ci => ItemUtilities.GetTargetPath(ci.OriginalItem),
                HandleRuntimeConflict);


            // resolve conflicts with platform (eg: shared framework) items
            // we only commit the platform items since its not a conflict if other items share the same filename.
            var platformConflictScope = new ConflictResolver(packageRanks, log);
            var platformItems = PlatformManifests?.SelectMany(pm => PlatformManifestReader.LoadConflictItems(pm.ItemSpec, log)) ?? Enumerable.Empty<ConflictItem>();

            platformConflictScope.ResolveConflicts(platformItems, pi => pi.FileName, pi => { });
            platformConflictScope.ResolveConflicts(referenceItems.Where(ri => !referenceConflicts.Contains(ri.OriginalItem)),
                                                   ri => ItemUtilities.GetReferenceTargetFileName(ri.OriginalItem),
                                                   HandleRuntimeConflict,
                                                   commitWinner:false);
            platformConflictScope.ResolveConflicts(copyLocalItems.Where(ci => !copyLocalConflicts.Contains(ci.OriginalItem)),
                                                   ri => ri.FileName,
                                                   HandleRuntimeConflict,
                                                   commitWinner: false);
            platformConflictScope.ResolveConflicts(otherRuntimeItems,
                                                   ri => ri.FileName,
                                                   HandleRuntimeConflict,
                                                   commitWinner: false);

            ReferencesWithoutConflicts = RemoveConflicts(References, referenceConflicts);
            ReferenceCopyLocalPathsWithoutConflicts = RemoveConflicts(ReferenceCopyLocalPaths, copyLocalConflicts);
            Conflicts = CreateConflictTaskItems(allConflicts);

            return !Log.HasLoggedErrors;
        }

        private ITaskItem[] CreateConflictTaskItems(ICollection<ConflictItem> conflicts)
        {
            var conflictItems = new ITaskItem[conflicts.Count];

            int i = 0;
            foreach(var conflict in conflicts)
            {
                conflictItems[i++] = CreateConflictTaskItem(conflict);
            }

            return conflictItems;
        }

        private ITaskItem CreateConflictTaskItem(ConflictItem conflict)
        {
            var item = new TaskItem(conflict.SourcePath);

            if (conflict.PackageId != null)
            {
                item.SetMetadata(nameof(ConflictItemType), conflict.ItemType.ToString());
            }

            return item;
        }

        private IEnumerable<ConflictItem> GetConflictTaskItems(ITaskItem[] items, ConflictItemType itemType)
        {
            return (items != null) ? items.Select(i => new ConflictItem(i, itemType)) : Enumerable.Empty<ConflictItem>();
        }

        private void HandleCompileConflict(ConflictItem conflictItem)
        {
            if (conflictItem.ItemType == ConflictItemType.Reference)
            {
                referenceConflicts.Add(conflictItem.OriginalItem);
            }
            allConflicts.Add(conflictItem);
        }

        private void HandleRuntimeConflict(ConflictItem conflictItem)
        {
            if (conflictItem.ItemType == ConflictItemType.Reference)
            {
                conflictItem.OriginalItem.SetMetadata(MetadataNames.Private, "False");
            }
            else if (conflictItem.ItemType == ConflictItemType.CopyLocal)
            {
                copyLocalConflicts.Add(conflictItem.OriginalItem);
            }
            allConflicts.Add(conflictItem);
        }

        /// <summary>
        /// Filters conflicts from original, maintaining order.
        /// </summary>
        /// <param name="original"></param>
        /// <param name="conflicts"></param>
        /// <returns></returns>
        private ITaskItem[] RemoveConflicts(ITaskItem[] original, ICollection<ITaskItem> conflicts)
        {
            if (conflicts.Count == 0)
            {
                return original;
            }

            var result = new ITaskItem[original.Length - conflicts.Count];
            int index = 0;

            foreach(var originalItem in original)
            {
                if (!conflicts.Contains(originalItem))
                {
                    if (index >= result.Length)
                    {
                        throw new ArgumentException($"Items from {nameof(conflicts)} were missing from {nameof(original)}");
                    }
                    result[index++] = originalItem;
                }
            }

            return result;
        }

        private class MSBuildLog : ILog
        {
            private TaskLoggingHelper logger;
            public MSBuildLog(TaskLoggingHelper logger)
            {
                this.logger = logger;
            }

            public void LogError(string errorMessage)
            {
                logger.LogError(errorMessage);
            }

            public void LogMessage(string message)
            {
                logger.LogMessage(message);
            }
        }

    }
}
