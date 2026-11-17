using EmbedIO;
using EmbedIO.Routing;
using EmbedIO.WebApi;
using NINA.Core.Model.Equipment;
using NINA.Core.Utility;
using NINA.Sequencer;
using NINA.Sequencer.Conditions;
using NINA.Sequencer.Container;
using NINA.Sequencer.Mediator;
using NINA.Sequencer.SequenceItem;
using NINA.Sequencer.SequenceItem.Utility;
using NINA.Sequencer.Serialization;
using NINA.Sequencer.Trigger;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Input;
using TouchNStars.Server.Models;
using Newtonsoft.Json;
using System.Threading.Tasks;
using System.Collections;

namespace TouchNStars.Server.Controllers
{
    /// <summary>
    /// API Controller for sequence item management and discovery
    /// </summary>
    public class SequenceController : WebApiController
    {
        // Unified ID tracking for all sequence objects (items, triggers, conditions)
        private enum ObjectType { Item, Trigger, Condition }

        private class TrackedObject
        {
            public object Value { get; set; }
            public ObjectType Type { get; set; }
            public long Seq { get; set; }
        }

        // Global ID registry for all sequence objects - STATIC so it persists across HTTP requests.
        // EmbedIO serves requests concurrently (status polling runs next to edits), so every access
        // goes through idLock. byObject is the reverse index that keeps lookups O(1) for big trees.
        private static readonly object idLock = new();
        private static readonly Dictionary<string, TrackedObject> objectIdMap = new();
        private static readonly Dictionary<object, string> idByObject = new(ReferenceEqualityComparer.Instance);
        private static long idCounter = 0;
        private static ISequenceRootContainer lastLoadedSequence = null;

        /// <summary>
        /// GET /api/sequence/items - List all available sequence items
        /// </summary>
        [Route(HttpVerbs.Get, "/sequence/items")]
        public SequenceItemsResponse ListSequenceItems()
        {
            try
            {
                var sequenceMediator = TouchNStars.Mediators?.Sequence;
                if (sequenceMediator == null)
                {
                    HttpContext.Response.StatusCode = 503;
                    return new SequenceItemsResponse
                    {
                        Success = false,
                        Error = "Sequence mediator not initialized",
                        Items = Array.Empty<SequenceItemMetadata>(),
                        Total = 0
                    };
                }

                var factory = GetFactory();

                if (factory?.Items == null || factory.Items.Count == 0)
                {
                    HttpContext.Response.StatusCode = 503;
                    return new SequenceItemsResponse
                    {
                        Success = false,
                        Error = "Sequence items not loaded yet",
                        Items = Array.Empty<SequenceItemMetadata>(),
                        Total = 0
                    };
                }
                var result = factory.Items
                    .Select(item => new SequenceItemMetadata
                    {
                        Name = item.Name ?? item.GetType().Name,
                        Description = item.Description ?? string.Empty,
                        Category = item.Category ?? "Uncategorized",
                        FullTypeName = item.GetType().FullName
                    })
                    .OrderBy(x => x.Category)
                    .ThenBy(x => x.Name)
                    .ToArray();

                HttpContext.Response.StatusCode = 200;
                return new SequenceItemsResponse
                {
                    Success = true,
                    Items = result,
                    Total = result.Length,
                    Error = null
                };
            }
            catch (Exception ex)
            {
                Logger.Error($"Error listing sequence items: {ex}");
                HttpContext.Response.StatusCode = 500;
                return new SequenceItemsResponse
                {
                    Success = false,
                    Error = $"Internal server error: {ex.Message}",
                    Items = Array.Empty<SequenceItemMetadata>(),
                    Total = 0
                };
            }
        }

        /// <summary>
        /// GET /api/sequence/triggers - List all available sequence triggers
        /// </summary>
        [Route(HttpVerbs.Get, "/sequence/triggers")]
        public SequenceItemsResponse ListSequenceTriggers()
        {
            try
            {
                var sequenceMediator = TouchNStars.Mediators?.Sequence;
                if (sequenceMediator == null)
                {
                    HttpContext.Response.StatusCode = 503;
                    return new SequenceItemsResponse
                    {
                        Success = false,
                        Error = "Sequence mediator not initialized",
                        Items = Array.Empty<SequenceItemMetadata>(),
                        Total = 0
                    };
                }

                // Use reflection to access private sequenceNavigation field (as shown in CoreUtility.cs)
                var factory = GetFactory();

                if (factory?.Triggers == null || factory.Triggers.Count == 0)
                {
                    HttpContext.Response.StatusCode = 503;
                    return new SequenceItemsResponse
                    {
                        Success = false,
                        Error = "Sequence triggers not loaded yet",
                        Items = Array.Empty<SequenceItemMetadata>(),
                        Total = 0
                    };
                }
                var result = factory.Triggers
                    .Select(trigger => new SequenceItemMetadata
                    {
                        Name = GetDisplayName(trigger),
                        Description = trigger.Description ?? string.Empty,
                        Category = trigger.Category ?? "Uncategorized",
                        FullTypeName = trigger.GetType().FullName
                    })
                    .OrderBy(x => x.Category)
                    .ThenBy(x => x.Name)
                    .ToArray();

                HttpContext.Response.StatusCode = 200;
                return new SequenceItemsResponse
                {
                    Success = true,
                    Items = result,
                    Total = result.Length,
                    Error = null
                };
            }
            catch (Exception ex)
            {
                Logger.Error($"Error listing sequence triggers: {ex}");
                HttpContext.Response.StatusCode = 500;
                return new SequenceItemsResponse
                {
                    Success = false,
                    Error = $"Internal server error: {ex.Message}",
                    Items = Array.Empty<SequenceItemMetadata>(),
                    Total = 0
                };
            }
        }

        /// <summary>
        /// GET /api/sequence/conditions - List all available sequence conditions
        /// </summary>
        [Route(HttpVerbs.Get, "/sequence/conditions")]
        public SequenceItemsResponse ListSequenceConditions()
        {
            try
            {
                var sequenceMediator = TouchNStars.Mediators?.Sequence;
                if (sequenceMediator == null)
                {
                    HttpContext.Response.StatusCode = 503;
                    return new SequenceItemsResponse
                    {
                        Success = false,
                        Error = "Sequence mediator not initialized",
                        Items = Array.Empty<SequenceItemMetadata>(),
                        Total = 0
                    };
                }

                // Use reflection to access private sequenceNavigation field (as shown in CoreUtility.cs)
                var factory = GetFactory();

                if (factory?.Conditions == null || factory.Conditions.Count == 0)
                {
                    HttpContext.Response.StatusCode = 503;
                    return new SequenceItemsResponse
                    {
                        Success = false,
                        Error = "Sequence conditions not loaded yet",
                        Items = Array.Empty<SequenceItemMetadata>(),
                        Total = 0
                    };
                }
                var result = factory.Conditions
                    .Select(condition => new SequenceItemMetadata
                    {
                        Name = GetDisplayName(condition),
                        Description = condition.Description ?? string.Empty,
                        Category = condition.Category ?? "Uncategorized",
                        FullTypeName = condition.GetType().FullName
                    })
                    .OrderBy(x => x.Category)
                    .ThenBy(x => x.Name)
                    .ToArray();

                HttpContext.Response.StatusCode = 200;
                return new SequenceItemsResponse
                {
                    Success = true,
                    Items = result,
                    Total = result.Length,
                    Error = null
                };
            }
            catch (Exception ex)
            {
                Logger.Error($"Error listing sequence conditions: {ex}");
                HttpContext.Response.StatusCode = 500;
                return new SequenceItemsResponse
                {
                    Success = false,
                    Error = $"Internal server error: {ex.Message}",
                    Items = Array.Empty<SequenceItemMetadata>(),
                    Total = 0
                };
            }
        }

        /// <summary>
        /// GET /api/sequence/date-time-providers - List all available date/time providers
        /// </summary>
        [Route(HttpVerbs.Get, "/sequence/date-time-providers")]
        public SequenceItemsResponse ListDateTimeProviders()
        {
            try
            {
                var factory = GetFactory();

                if (factory?.DateTimeProviders == null || factory.DateTimeProviders.Count == 0)
                {
                    HttpContext.Response.StatusCode = 503;
                    return new SequenceItemsResponse
                    {
                        Success = false,
                        Error = "Date/time providers not loaded yet",
                        Items = Array.Empty<SequenceItemMetadata>(),
                        Total = 0
                    };
                }

                var result = factory.DateTimeProviders
                    .Select(provider => new SequenceItemMetadata
                    {
                        Name = provider.Name ?? provider.GetType().Name,
                        Description = "DateTimeProvider",
                        Category = "DateTimeProvider",
                        FullTypeName = provider.GetType().FullName
                    })
                    .OrderBy(x => x.Name)
                    .ToArray();

                HttpContext.Response.StatusCode = 200;
                return new SequenceItemsResponse
                {
                    Success = true,
                    Items = result,
                    Total = result.Length,
                    Error = null
                };
            }
            catch (Exception ex)
            {
                Logger.Error($"Error listing date/time providers: {ex}");
                HttpContext.Response.StatusCode = 500;
                return new SequenceItemsResponse
                {
                    Success = false,
                    Error = $"Internal server error: {ex.Message}",
                    Items = Array.Empty<SequenceItemMetadata>(),
                    Total = 0
                };
            }
        }

        /// <summary>
        /// GET /api/sequence/files - List all available sequence files
        /// </summary>
        [Route(HttpVerbs.Get, "/sequence/files")]
        public SequenceListResponse ListSequenceFiles([QueryField] string folderPath = null)
        {
            try
            {
                var sequenceFiles = new List<SequenceFileInfo>();
                var searchDirectories = new List<string>();

                // An explicit folder from the client wins as long as it lies inside the profile sequence
                // directory (a subfolder); anything else would let a client crawl the whole disk.
                var profileDir = GetSequenceFolder();
                bool clientFolderAllowed = false;
                if (!string.IsNullOrEmpty(folderPath) && !string.IsNullOrEmpty(profileDir) && Directory.Exists(folderPath))
                {
                    try
                    {
                        var fullFolder = Path.GetFullPath(folderPath);
                        clientFolderAllowed = string.Equals(fullFolder.TrimEnd('\\', '/'), Path.GetFullPath(profileDir).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)
                            || IsInsideFolder(fullFolder, profileDir);
                    }
                    catch { /* invalid path - use the profile folder */ }
                }
                var sequenceDir = clientFolderAllowed ? folderPath : profileDir;
                if (!string.IsNullOrEmpty(sequenceDir) && Directory.Exists(sequenceDir))
                {
                    searchDirectories.Add(sequenceDir);
                }

                // Search all directories for .seq files
                foreach (var directory in searchDirectories)
                {
                    if (Directory.Exists(directory))
                    {
                        try
                        {
                            var files = Directory.GetFiles(directory, "*.json", SearchOption.AllDirectories)
                                .Where(f => !f.Contains("AutoFocus", StringComparison.OrdinalIgnoreCase));

                            foreach (var file in files)
                            {
                                try
                                {
                                    var fileInfo = new FileInfo(file);
                                    sequenceFiles.Add(new SequenceFileInfo
                                    {
                                        FileName = fileInfo.Name,
                                        FilePath = file,
                                        Name = Path.GetFileNameWithoutExtension(file),
                                        LastModified = fileInfo.LastWriteTime,
                                        FileSize = fileInfo.Length
                                    });
                                }
                                catch (Exception ex)
                                {
                                    Logger.Warning($"Error reading sequence file info for {file}: {ex.Message}");
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Logger.Warning($"Error searching directory {directory}: {ex.Message}");
                        }
                    }
                }

                HttpContext.Response.StatusCode = 200;
                return new SequenceListResponse
                {
                    Success = true,
                    Sequences = sequenceFiles.OrderByDescending(s => s.LastModified).ToList(),
                    Error = null
                };
            }
            catch (Exception ex)
            {
                Logger.Error($"Error listing sequence files: {ex}");
                HttpContext.Response.StatusCode = 500;
                return new SequenceListResponse
                {
                    Success = false,
                    Error = $"Internal server error: {ex.Message}",
                    Sequences = new List<SequenceFileInfo>()
                };
            }
        }

        /// <summary>
        /// GET /api/sequence/load?filePath=path/to/sequence.json - Load a sequence file into NINA
        /// </summary>
        [Route(HttpVerbs.Get, "/sequence/load")]
        public ApiResponse LoadSequenceFile([QueryField] string filePath)
        {
            try
            {
                if (string.IsNullOrEmpty(filePath))
                {
                    HttpContext.Response.StatusCode = 400;
                    return new ApiResponse
                    {
                        Success = false,
                        Error = "filePath parameter is required",
                        StatusCode = 400,
                        Type = "Error"
                    };
                }

                if (!TryResolveSequenceFile(filePath, out var resolvedPath, out var pathRejection))
                    return pathRejection;
                filePath = resolvedPath;

                if (!File.Exists(filePath))
                {
                    HttpContext.Response.StatusCode = 404;
                    return new ApiResponse
                    {
                        Success = false,
                        Error = $"Sequence file not found: {filePath}",
                        StatusCode = 404,
                        Type = "Error"
                    };
                }

                // Get the sequence mediator
                var sequenceMediator = TouchNStars.Mediators?.Sequence;
                if (sequenceMediator == null || !sequenceMediator.Initialized)
                {
                    HttpContext.Response.StatusCode = 503;
                    return new ApiResponse
                    {
                        Success = false,
                        Error = "Sequence mediator not initialized",
                        StatusCode = 503,
                        Type = "Error"
                    };
                }

                if (sequenceMediator.IsAdvancedSequenceRunning())
                {
                    HttpContext.Response.StatusCode = 409;
                    return new ApiResponse
                    {
                        Success = false,
                        Error = "Cannot load sequence while one is running",
                        StatusCode = 409,
                        Type = "Error"
                    };
                }

                var factory = GetFactory();

                if (factory == null)
                {
                    HttpContext.Response.StatusCode = 503;
                    return new ApiResponse
                    {
                        Success = false,
                        Error = "Unable to access sequence factory",
                        StatusCode = 503,
                        Type = "Error"
                    };
                }

                // Use NINA's SequenceJsonConverter to properly deserialize the sequence
                var converter = new SequenceJsonConverter(factory);
                string jsonContent = File.ReadAllText(filePath);
                var container = converter.Deserialize(jsonContent);

                if (container == null)
                {
                    HttpContext.Response.StatusCode = 400;
                    return new ApiResponse
                    {
                        Success = false,
                        Error = "Unable to deserialize sequence file",
                        StatusCode = 400,
                        Type = "Error"
                    };
                }

                // Handle different container types
                SequenceRootContainer root;
                if (container is DeepSkyObjectContainer dso)
                {
                    // Wrap DSO in a proper sequence structure
                    root = factory.GetContainer<SequenceRootContainer>();
                    root.Name = dso.Name;
                    root.Add(factory.GetContainer<StartAreaContainer>());
                    var targetArea = factory.GetContainer<TargetAreaContainer>();
                    targetArea.Add(dso);
                    root.Add(targetArea);
                    root.Add(factory.GetContainer<EndAreaContainer>());
                }
                else if (container is SequenceRootContainer sequenceRoot)
                {
                    root = sequenceRoot;
                }
                else
                {
                    HttpContext.Response.StatusCode = 400;
                    return new ApiResponse
                    {
                        Success = false,
                        Error = $"Unsupported container type: {container.GetType().Name}",
                        StatusCode = 400,
                        Type = "Error"
                    };
                }

                // Load the sequence into NINA using the dispatcher
                try
                {
                    Application.Current.Dispatcher.Invoke(() => sequenceMediator.SetAdvancedSequence(root));
                    ResetIdCounterAndMap();
                }
                catch (Exception ex)
                {
                    Logger.Error($"Error loading sequence into mediator: {ex}");
                    HttpContext.Response.StatusCode = 500;
                    return new ApiResponse
                    {
                        Success = false,
                        Error = $"Failed to load sequence into NINA: {ex.Message}",
                        StatusCode = 500,
                        Type = "Error"
                    };
                }

                HttpContext.Response.StatusCode = 200;
                return new ApiResponse
                {
                    Success = true,
                    StatusCode = 200,
                    Type = "SequenceLoaded"
                };
            }
            catch (Exception ex)
            {
                Logger.Error($"Error loading sequence file: {ex}");
                HttpContext.Response.StatusCode = 500;
                return new ApiResponse
                {
                    Success = false,
                    Error = $"Internal server error: {ex.Message}",
                    StatusCode = 500,
                    Type = "Error"
                };
            }
        }

        /// <summary>
        /// GET /api/sequence/current - Get the current sequence loaded in NINA (similar to ninaAPI)
        /// </summary>
        [Route(HttpVerbs.Get, "/sequence/current")]
        public object GetCurrentSequence()
        {
            try
            {
                var sequenceMediator = TouchNStars.Mediators?.Sequence;
                if (sequenceMediator == null || !sequenceMediator.Initialized)
                {
                    HttpContext.Response.StatusCode = 400;
                    return new
                    {
                        Success = false,
                        Error = "No sequence loaded",
                        StatusCode = 400,
                        Type = "Error"
                    };
                }

                try
                {
                    var mainContainer = GetMainContainer();

                    if (mainContainer == null)
                    {
                        HttpContext.Response.StatusCode = 400;
                        return new
                        {
                            Success = false,
                            Error = "No sequence loaded",
                            StatusCode = 400,
                            Type = "Error"
                        };
                    }

                    EnsureIdScope(mainContainer);

                    // Plugins like Target Scheduler add and remove items while the sequence runs,
                    // which can break the enumeration half way - retry once on a fresh walk.
                    List<Hashtable> sequenceData = RetryOnConcurrentModification<List<Hashtable>>(() =>
                    [
                        new Hashtable() {
                            { "Id", GetOrCreateId(mainContainer, ObjectType.Item) },
                            { "GlobalTriggers", getTriggers((SequenceContainer)mainContainer) }
                        },
                        .. getSequenceRecursively(mainContainer),
                    ]); // Global triggers

                    HttpContext.Response.StatusCode = 200;
                    WriteSequenceResponseData(HttpContext, sequenceData);

                    return null;
                }
                catch (Exception ex)
                {
                    Logger.Error($"Error accessing current sequence: {ex}");
                    HttpContext.Response.StatusCode = 500;
                    return new
                    {
                        Success = false,
                        Error = $"Failed to get current sequence: {ex.Message}",
                        StatusCode = 500,
                        Type = "Error"
                    };
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Error getting current sequence: {ex}");
                HttpContext.Response.StatusCode = 500;
                return new
                {
                    Success = false,
                    Error = $"Internal server error: {ex.Message}",
                    StatusCode = 500,
                    Type = "Error"
                };
            }
        }

        // Fields that change while a sequence runs. Kept in sync with RUNTIME_FIELDS in the
        // Touch'N'Stars frontend (src/store/sequenceV2Store.js).
        private static readonly string[] RuntimeFieldNames = {
            "ExpectedTime", "ExpectedTimeStr", "CurrentAltitude", "CurrentIllumination", "TargetIllumination",
            "CurrentMoonIllumination", "UserMoonIllumination", "CompletedIterations", "RemainingTime",
            "TargetTime", "InterruptReason", "Progress" };

        /// <summary>
        /// GET /api/sequence/status - Lightweight poll endpoint.
        /// Returns a flat list of {Id, Status, runtime fields} for every item, trigger and condition,
        /// plus a Revision hash over the tree structure. The client only has to reload
        /// /sequence/current when Revision changes; otherwise it merges the list by Id.
        /// </summary>
        [Route(HttpVerbs.Get, "/sequence/status")]
        public object GetSequenceStatus()
        {
            try
            {
                var sequenceMediator = TouchNStars.Mediators?.Sequence;
                var mainContainer = sequenceMediator != null && sequenceMediator.Initialized ? GetMainContainer() : null;
                if (mainContainer == null)
                {
                    HttpContext.Response.StatusCode = 400;
                    return new { Success = false, Error = "No sequence loaded", StatusCode = 400, Type = "Error" };
                }

                EnsureIdScope(mainContainer);

                var walkStartSeq = CurrentIdSeq();
                var (entries, revision, seen) = RetryOnConcurrentModification(() =>
                {
                    var list = new List<Hashtable>();
                    var structure = new System.Text.StringBuilder();
                    var visited = new HashSet<object>(ReferenceEqualityComparer.Instance) { mainContainer };
                    CollectStatus(mainContainer, list, structure, visited);
                    return (list, HashStructure(structure.ToString()), visited);
                });
                PruneRegistry(seen, walkStartSeq);

                HttpContext.Response.StatusCode = 200;
                WriteSequenceResponseData(HttpContext, new Hashtable
                {
                    { "Success", true },
                    { "Revision", revision },
                    { "Running", sequenceMediator.IsAdvancedSequenceRunning() },
                    { "Items", entries }
                });
                return null;
            }
            catch (Exception ex)
            {
                Logger.Error($"Error getting sequence status: {ex}");
                HttpContext.Response.StatusCode = 500;
                return new { Success = false, Error = $"Internal server error: {ex.Message}", StatusCode = 500, Type = "Error" };
            }
        }

        private static void CollectStatus(ISequenceContainer container, List<Hashtable> list, System.Text.StringBuilder structure,
            HashSet<object> visited)
        {
            var containerId = GetOrCreateId(container, ObjectType.Item);

            // Enumerate the live collections instead of ToArray(): their enumerator reliably throws
            // InvalidOperationException on a concurrent change (-> retry), while copying a list that
            // is being resized can silently yield missing or null entries.
            if (container is SequenceContainer sc)
            {
                foreach (var trigger in sc.Triggers)
                {
                    visited.Add(trigger);
                    AddStatusEntry(trigger, GetOrCreateId(trigger, ObjectType.Trigger), trigger.Status, containerId, "T", list, structure);
                }
                foreach (var condition in sc.Conditions)
                {
                    visited.Add(condition);
                    AddStatusEntry(condition, GetOrCreateId(condition, ObjectType.Condition), condition.Status, containerId, "C", list, structure);
                }
            }

            foreach (var item in container.Items)
            {
                visited.Add(item);
                var id = GetOrCreateId(item, ObjectType.Item);
                AddStatusEntry(item, id, item.Status, containerId, "I", list, structure);
                if (item is ISequenceContainer child)
                    CollectStatus(child, list, structure, visited);
            }
        }

        private static void AddStatusEntry(object obj, string id, NINA.Core.Enum.SequenceEntityStatus status, string parentId,
            string kind, List<Hashtable> list, System.Text.StringBuilder structure)
        {
            structure.Append(parentId).Append('>').Append(kind).Append(id).Append(';');

            var entry = new Hashtable
            {
                { "Id", id },
                { "Status", status.ToString() }
            };

            var type = obj.GetType();
            object data = null;
            try { data = type.GetProperty("Data", BindingFlags.Public | BindingFlags.Instance)?.GetValue(obj); }
            catch { /* no WaitLoopData on this entity */ }

            foreach (var name in RuntimeFieldNames)
            {
                try
                {
                    var prop = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                    object owner = obj;
                    if (prop == null && data != null)
                    {
                        prop = data.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                        owner = data;
                    }
                    if (prop == null || prop.GetIndexParameters().Length > 0)
                        continue;
                    var value = prop.GetValue(owner);
                    entry[name] = value is TimeSpan ts ? ts.ToString(@"hh\:mm\:ss") : SafeSerializeValue(value);
                }
                catch { /* skip fields that throw */ }
            }

            list.Add(entry);
        }

        /// <summary>
        /// Stable 64-bit FNV-1a hash, rendered as hex. string.GetHashCode() is randomized per
        /// process, so it cannot be used for a value clients compare across requests.
        /// </summary>
        private static string HashStructure(string text)
        {
            ulong hash = 14695981039346656037UL;
            foreach (char c in text)
            {
                hash ^= c;
                hash *= 1099511628211UL;
            }
            return hash.ToString("x16");
        }

        private static T RetryOnConcurrentModification<T>(Func<T> read)
        {
            const int maxAttempts = 3;
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    return read();
                }
                catch (InvalidOperationException ex) when (attempt < maxAttempts)
                {
                    Logger.Debug($"Sequence changed while reading, retrying: {ex.Message}");
                }
            }
        }

        private static string GetSequenceFolder()
        {
            return TouchNStars.Mediators?.Profile?.ActiveProfile?.SequenceSettings?.DefaultSequenceFolder;
        }

        private static bool IsInsideFolder(string fullPath, string folder)
        {
            var root = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                       + Path.DirectorySeparatorChar;
            return fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Sequence files may only be read, written and deleted as .json files below the sequence
        /// folder of the active profile. Without this a client could delete or overwrite any file
        /// NINA can access, including its own profiles. Relative paths resolve against that folder.
        /// </summary>
        private bool TryResolveSequenceFile(string filePath, out string fullPath, out ApiResponse rejection)
        {
            fullPath = null;
            rejection = null;
            string error = null;

            var folder = GetSequenceFolder();
            if (string.IsNullOrWhiteSpace(folder))
            {
                error = "No sequence folder is configured in the active NINA profile";
            }
            else
            {
                try
                {
                    var combined = Path.IsPathRooted(filePath) ? filePath : Path.Combine(folder, filePath);
                    fullPath = Path.GetFullPath(combined);
                }
                catch (Exception ex)
                {
                    error = $"Invalid file path: {ex.Message}";
                }

                if (error == null && !IsInsideFolder(fullPath, folder))
                    error = $"Sequence files must be inside the sequence folder '{folder}'";
                else if (error == null && !string.Equals(Path.GetExtension(fullPath), ".json", StringComparison.OrdinalIgnoreCase))
                    error = "Only .json sequence files are allowed";
            }

            if (error == null)
                return true;

            fullPath = null;
            HttpContext.Response.StatusCode = 400;
            rejection = new ApiResponse { Success = false, Error = error, StatusCode = 400, Type = "Error" };
            return false;
        }

        private static bool IsRunning(object obj)
        {
            return obj is ISequenceEntity entity && entity.Status == NINA.Core.Enum.SequenceEntityStatus.RUNNING;
        }

        /// <summary>
        /// The node the sequencer is executing right now must not be changed, moved, disabled,
        /// reset or removed. Children of a running container stay editable, like in NINA itself.
        /// The client hides those actions as well, but its status comes from a poll and can lag.
        /// Returns the 409 response to send, or null when the object may be changed.
        /// </summary>
        private ApiResponse RejectIfRunning(object obj)
        {
            if (!IsRunning(obj))
                return null;
            HttpContext.Response.StatusCode = 409;
            return new ApiResponse
            {
                Success = false,
                Error = "The item is currently running and cannot be changed",
                StatusCode = 409,
                Type = "Error"
            };
        }

        /// <summary>
        /// POST /api/sequence/move - Move a sequence item, trigger, or condition before or after a target (by ID)
        /// id: ID of the object to move (item, trigger, or condition)
        /// targetId: ID of the target object to move before/after. It may sit in another container than
        ///           the moved object, which then changes its parent (IDs, children and settings are kept).
        /// insertAfter: if true, move after target; if false, move before.
        ///              When targetId is a container: omit insertAfter to move INTO it (position 0 of its
        ///              items, triggers or conditions, matching the moved object); otherwise default true.
        /// </summary>
        [Route(HttpVerbs.Post, "/sequence/move")]
        public ApiResponse MoveSequenceItem([QueryField] string id, [QueryField] string targetId, [QueryField] bool? insertAfter = null)
        {
            try
            {
                if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(targetId))
                {
                    HttpContext.Response.StatusCode = 400;
                    return new ApiResponse
                    {
                        Success = false,
                        Error = "id and targetId parameters are required",
                        StatusCode = 400,
                        Type = "Error"
                    };
                }

                var sequenceMediator = TouchNStars.Mediators?.Sequence;
                if (sequenceMediator == null || !sequenceMediator.Initialized)
                {
                    HttpContext.Response.StatusCode = 503;
                    return new ApiResponse
                    {
                        Success = false,
                        Error = "Sequence mediator not initialized",
                    };
                }

                try
                {
                    // Find the objects to move using the unified FindObjectById
                    var objectToMove = FindObjectById(id);
                    if (objectToMove == null)
                    {
                        HttpContext.Response.StatusCode = 404;
                        return new ApiResponse
                        {
                            Success = false,
                            Error = $"Object to move not found with ID: {id}",
                        };
                    }

                    var runningRejection = RejectIfRunning(objectToMove);
                    if (runningRejection != null)
                        return runningRejection;

                    // Find the target object
                    var targetObject = FindObjectById(targetId);
                    if (targetObject == null)
                    {
                        HttpContext.Response.StatusCode = 404;
                        return new ApiResponse
                        {
                            Success = false,
                            Error = $"Target object not found with ID: {targetId}",
                        };
                    }

                    // Determine object types
                    bool isItem = objectToMove is ISequenceItem;
                    bool isTrigger = objectToMove is ISequenceTrigger;
                    bool isCondition = objectToMove is ISequenceCondition;

                    var mainContainer = GetMainContainer();
                    if (mainContainer == null)
                        return MoveError(400, "No sequence loaded");

                    // A container target without insertAfter means "move into it", like /sequence/add.
                    // The client needs that to drop into an empty list, where there is no sibling to
                    // position against. Triggers and conditions go into the container's own lists.
                    if (insertAfter == null && targetObject is ISequenceContainer intoContainer)
                    {
                        if (isItem)
                            return MoveItem((ISequenceItem)objectToMove, intoContainer, 0);
                        if (isTrigger)
                            return MoveTrigger((ISequenceTrigger)objectToMove, intoContainer, 0);
                        if (isCondition)
                            return MoveCondition((ISequenceCondition)objectToMove, intoContainer, 0);
                    }

                    bool targetIsItem = targetObject is ISequenceItem;
                    bool targetIsTrigger = targetObject is ISequenceTrigger;
                    bool targetIsCondition = targetObject is ISequenceCondition;

                    // Ensure both objects are the same type
                    if ((isItem && !targetIsItem) || (isTrigger && !targetIsTrigger) || (isCondition && !targetIsCondition))
                        return MoveError(400, "Cannot move objects of different types");

                    int offset = (insertAfter ?? true) ? 1 : 0;

                    // The target sibling may sit in another container than the moved object; the
                    // object then changes its parent. The index is counted before the object is
                    // taken out of its old position.
                    if (isItem)
                    {
                        var targetItem = (ISequenceItem)targetObject;
                        ISequenceContainer targetParent = null;
                        FindItemContainer(mainContainer, targetItem, ref targetParent);
                        if (targetParent == null)
                            return MoveError(404, "Could not find position of target in sequence");
                        return MoveItem((ISequenceItem)objectToMove, targetParent, targetParent.Items.IndexOf(targetItem) + offset);
                    }
                    if (isTrigger)
                    {
                        var targetTrigger = (ISequenceTrigger)targetObject;
                        var targetParent = FindTriggerContainer(mainContainer, targetTrigger) as SequenceContainer;
                        if (targetParent == null)
                            return MoveError(404, "Target trigger not found in sequence");
                        return MoveTrigger((ISequenceTrigger)objectToMove, targetParent, targetParent.Triggers.IndexOf(targetTrigger) + offset);
                    }
                    if (isCondition)
                    {
                        var targetCondition = (ISequenceCondition)targetObject;
                        var targetParent = FindConditionContainer(mainContainer, targetCondition) as SequenceContainer;
                        if (targetParent == null)
                            return MoveError(404, "Target condition not found in sequence");
                        return MoveCondition((ISequenceCondition)objectToMove, targetParent, targetParent.Conditions.IndexOf(targetCondition) + offset);
                    }

                    return MoveError(400, "Unknown object type");
                }
                catch (Exception ex)
                {
                    Logger.Error($"Error moving sequence object: {ex}");
                    HttpContext.Response.StatusCode = 500;
                    return new ApiResponse
                    {
                        Success = false,
                        Error = $"Failed to move object: {ex.Message}",
                        StatusCode = 500,
                        Type = "Error"
                    };
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Error in move endpoint: {ex}");
                HttpContext.Response.StatusCode = 500;
                return new ApiResponse
                {
                    Success = false,
                    Error = $"Internal server error: {ex.Message}",
                    StatusCode = 500,
                    Type = "Error"
                };
            }
        }

        /// <summary>
        /// POST /api/sequence/add - Add a new object (item, trigger, or condition) to the sequence
        /// targetId: ID of the target (item to add before/after, or container to add to)
        /// type: Type name of the object to add (automatically detects if it's an item, trigger, or condition)
        /// insertAfter: if true, insert after target; if false, insert before (only for items, ignored for triggers/conditions).
        ///              When targetId is a container: omit insertAfter to add INSIDE the container;
        ///              provide insertAfter=true/false to add AFTER/BEFORE the container as a sibling.
        /// </summary>
        [Route(HttpVerbs.Post, "/sequence/add")]
        public ApiResponse AddObject([QueryField] string targetId, [QueryField] string type, [QueryField] bool? insertAfter = null)
        {
            try
            {
                if (string.IsNullOrEmpty(targetId) || string.IsNullOrEmpty(type))
                {
                    HttpContext.Response.StatusCode = 400;
                    return new ApiResponse
                    {
                        Success = false,
                        Error = "targetId and type parameters are required",
                    };
                }

                var factory = GetFactory();
                if (factory == null)
                {
                    HttpContext.Response.StatusCode = 503;
                    return new ApiResponse
                    {
                        Success = false,
                        Error = "Factory not initialized",
                    };
                }

                // Detect what type this is - check factory collections first
                var itemTemplate = factory.Items?.FirstOrDefault(i => i.GetType().Name == type || i.GetType().FullName == type);
                var triggerTemplate = factory.Triggers?.FirstOrDefault(t => t.GetType().Name == type || t.GetType().FullName == type);
                var conditionTemplate = factory.Conditions?.FirstOrDefault(c => c.GetType().Name == type || c.GetType().FullName == type);

                if (itemTemplate != null)
                {
                    // It's an item - use the item add logic
                    return AddSequenceItem(targetId, itemTemplate.GetType().FullName, insertAfter);
                }
                else if (triggerTemplate != null)
                {
                    // It's a trigger - use the trigger add logic
                    return AddTrigger(targetId, type, insertAfter);
                }
                else if (conditionTemplate != null)
                {
                    // It's a condition - use the condition add logic
                    return AddCondition(targetId, type, insertAfter);
                }
                else
                {
                    // Only types offered by NINA's sequencer factory can be added. Instantiating a
                    // client-supplied type name would run arbitrary constructors and create items
                    // without the dependencies the factory injects.
                    HttpContext.Response.StatusCode = 400;
                    return new ApiResponse
                    {
                        Success = false,
                        Error = $"Type '{type}' not found in factory (not an item, trigger, or condition)",
                    };
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Error in add endpoint: {ex}");
                HttpContext.Response.StatusCode = 500;
                return new ApiResponse
                {
                    Success = false,
                    Error = $"Internal server error: {ex.Message}",
                };
            }
        }
        private ApiResponse MoveError(int statusCode, string error)
        {
            HttpContext.Response.StatusCode = statusCode;
            return new ApiResponse { Success = false, Error = error, StatusCode = statusCode, Type = "Error" };
        }

        private ApiResponse MoveSuccess()
        {
            HttpContext.Response.StatusCode = 200;
            return new ApiResponse { Success = true, Error = null, StatusCode = 200, Type = "Success" };
        }

        /// <summary>
        /// Whether candidate is ancestor itself or one of its nested containers
        /// </summary>
        private static bool IsSameOrNestedContainer(ISequenceContainer ancestor, ISequenceContainer candidate)
        {
            if (ancestor == candidate)
                return true;
            foreach (var child in ancestor.Items)
            {
                if (child is ISequenceContainer childContainer && IsSameOrNestedContainer(childContainer, candidate))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Moves an entity inside one list or from one list into another. index is the position in
        /// the target list counted before the entity is taken out. Runs on the UI thread.
        /// </summary>
        private static void MoveBetweenLists<T>(IList<T> source, IList<T> target, T entity, int index)
        {
            int currentIndex = source.IndexOf(entity);
            if (currentIndex < 0)
                return;
            if (source == target && currentIndex < index)
                index--;
            source.RemoveAt(currentIndex);
            target.Insert(Math.Max(0, Math.Min(index, target.Count)), entity);
        }

        /// <summary>
        /// Move a sequence item to a position in a container, which may be another container than
        /// its current one. The object itself is moved, not a clone, so its ID, children, triggers,
        /// conditions and settings are kept.
        /// </summary>
        private ApiResponse MoveItem(ISequenceItem itemToMove, ISequenceContainer targetContainer, int index)
        {
            try
            {
                var mainContainer = GetMainContainer();
                if (mainContainer == null)
                    return MoveError(400, "No sequence loaded");

                ISequenceContainer sourceContainer = null;
                FindItemContainer(mainContainer, itemToMove, ref sourceContainer);
                if (sourceContainer == null)
                    return MoveError(404, "Item to move not found in sequence");

                if (sourceContainer == targetContainer)
                {
                    int currentIndex = sourceContainer.Items.IndexOf(itemToMove);
                    int targetIndex = currentIndex < index ? index - 1 : index;

                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        if (sourceContainer is SequenceContainer seqContainer &&
                            currentIndex >= 0 && currentIndex < seqContainer.Items.Count &&
                            targetIndex >= 0 && targetIndex <= seqContainer.Items.Count &&
                            currentIndex != targetIndex)
                        {
                            seqContainer.MoveWithinIntoSequenceBlocks(currentIndex, targetIndex);
                        }
                    });
                    return MoveSuccess();
                }

                // The start, target and end areas are the root's only children and stay in place.
                if (sourceContainer == mainContainer || targetContainer == mainContainer)
                    return MoveError(400, "Items cannot be moved into or out of the sequence root");

                if (itemToMove is ISequenceContainer movedContainer && IsSameOrNestedContainer(movedContainer, targetContainer))
                    return MoveError(400, "A container cannot be moved into itself");

                if (!(targetContainer is SequenceContainer targetSeqContainer))
                    return MoveError(400, "The target cannot hold items");

                Application.Current.Dispatcher.Invoke(() =>
                {
                    // NINA's Remove/Add detach and re-attach the parent, like the existing add and
                    // remove endpoints. The item stays tracked, so its ID does not change.
                    if (sourceContainer is SequenceContainer sourceSeqContainer)
                        sourceSeqContainer.Remove(itemToMove);
                    else
                        sourceContainer.Items.Remove(itemToMove);

                    targetSeqContainer.Add(itemToMove);
                    int lastIndex = targetSeqContainer.Items.Count - 1;
                    int destination = Math.Max(0, Math.Min(index, lastIndex));
                    if (destination != lastIndex)
                        targetSeqContainer.MoveWithinIntoSequenceBlocks(lastIndex, destination);
                });

                return MoveSuccess();
            }
            catch (Exception ex)
            {
                Logger.Error($"Error moving item: {ex}");
                return MoveError(500, $"Failed to move item: {ex.Message}");
            }
        }

        /// <summary>
        /// Move a trigger to a position in a container's triggers, which may belong to another
        /// container than its current one
        /// </summary>
        private ApiResponse MoveTrigger(ISequenceTrigger triggerToMove, ISequenceContainer targetContainer, int index)
        {
            try
            {
                var mainContainer = GetMainContainer();
                if (mainContainer == null)
                    return MoveError(400, "No sequence loaded");

                if (!(FindTriggerContainer(mainContainer, triggerToMove) is SequenceContainer sourceContainer))
                    return MoveError(404, "Trigger to move not found in sequence");
                if (!(targetContainer is SequenceContainer targetSeqContainer))
                    return MoveError(400, "The target cannot hold triggers");

                Application.Current.Dispatcher.Invoke(() =>
                {
                    MoveBetweenLists(sourceContainer.Triggers, targetSeqContainer.Triggers, triggerToMove, index);
                    if (sourceContainer != targetSeqContainer)
                        triggerToMove.AttachNewParent(targetSeqContainer);
                });

                return MoveSuccess();
            }
            catch (Exception ex)
            {
                Logger.Error($"Error moving trigger: {ex}");
                return MoveError(500, $"Failed to move trigger: {ex.Message}");
            }
        }

        /// <summary>
        /// Move a condition to a position in a container's conditions, which may belong to another
        /// container than its current one
        /// </summary>
        private ApiResponse MoveCondition(ISequenceCondition conditionToMove, ISequenceContainer targetContainer, int index)
        {
            try
            {
                var mainContainer = GetMainContainer();
                if (mainContainer == null)
                    return MoveError(400, "No sequence loaded");

                if (!(FindConditionContainer(mainContainer, conditionToMove) is SequenceContainer sourceContainer))
                    return MoveError(404, "Condition to move not found in sequence");
                if (!(targetContainer is SequenceContainer targetSeqContainer))
                    return MoveError(400, "The target cannot hold conditions");

                Application.Current.Dispatcher.Invoke(() =>
                {
                    MoveBetweenLists(sourceContainer.Conditions, targetSeqContainer.Conditions, conditionToMove, index);
                    if (sourceContainer != targetSeqContainer)
                        conditionToMove.AttachNewParent(targetSeqContainer);
                });

                return MoveSuccess();
            }
            catch (Exception ex)
            {
                Logger.Error($"Error moving condition: {ex}");
                return MoveError(500, $"Failed to move condition: {ex.Message}");
            }
        }

        /// <summary>
        /// Internal method to add a new sequence item before/after a target item, or to a container (by ID)
        /// targetId: ID of the item (add before/after) or container (add to beginning)
        /// itemType: Full type name of the item to add
        /// insertAfter: if true, insert after the target item; if false, insert before.
        ///              For container targets: omit (null) to add INSIDE; provide true/false to add AFTER/BEFORE as a sibling.
        /// </summary>
        private ApiResponse AddSequenceItem(string targetId, string itemType, bool? insertAfter = null)
        {
            try
            {
                if (string.IsNullOrEmpty(targetId) || string.IsNullOrEmpty(itemType))
                {
                    HttpContext.Response.StatusCode = 400;
                    return new ApiResponse
                    {
                        Success = false,
                        Error = "targetId and itemType parameters are required",
                    };
                }

                // Default to true if not provided
                bool shouldInsertAfter = insertAfter ?? true;

                var sequenceMediator = TouchNStars.Mediators?.Sequence;
                if (sequenceMediator == null || !sequenceMediator.Initialized)
                {
                    HttpContext.Response.StatusCode = 503;
                    return new ApiResponse
                    {
                        Success = false,
                        Error = "Sequence mediator not initialized",
                    };
                }

                try
                {
                    // Find the target by ID
                    var targetItem = FindItemById(targetId);
                    if (targetItem == null)
                    {
                        HttpContext.Response.StatusCode = 404;
                        return new ApiResponse
                        {
                            Success = false,
                            Error = $"Target item not found with ID: {targetId}",
                        };
                    }

                    // Get the template item from the factory
                    ISequenceItem templateItem = null;
                    var factory = GetFactory();
                    if (factory?.Items != null)
                    {
                        templateItem = factory.Items.FirstOrDefault(item => item.GetType().FullName == itemType || item.GetType().Name == itemType);
                    }

                    if (templateItem == null)
                    {
                        HttpContext.Response.StatusCode = 400;
                        return new ApiResponse
                        {
                            Success = false,
                            Error = $"Could not find template item of type: {itemType}",
                        };
                    }

                    // Validate that the item type is compatible with the target location
                    // Only ISequenceItem (and implementations) can be added to the Items collection
                    if (!(templateItem is ISequenceItem))
                    {
                        HttpContext.Response.StatusCode = 400;
                        return new ApiResponse
                        {
                            Success = false,
                            Error = $"Item type '{itemType}' is not a valid sequence item. Only ISequenceItem implementations can be added.",
                        };
                    }

                    // Check if target is a regular container (type name ends with "Container")
                    // Only regular containers like SequentialContainer, StartAreaContainer, etc. can accept new items into them
                    bool isRegularContainer = targetItem.GetType().Name.EndsWith("Container");
                    var targetContainer = targetItem as ISequenceContainer;
                    string createdItemId = null;

                    // When insertAfter is explicitly provided for a container target, the caller wants to
                    // insert the new item as a sibling (before/after the container) rather than inside it.
                    bool addInsideContainer = isRegularContainer && targetContainer != null && insertAfter == null;

                    if (addInsideContainer)
                    {
                        // Add to container at position 0 (beginning)
                        Application.Current.Dispatcher.Invoke(() =>
                        {
                            var clonedItem = CloneSequenceItem(templateItem);
                            if (clonedItem != null)
                            {
                                targetContainer.Add(clonedItem);

                                // Move to position 0
                                if (targetContainer is SequenceContainer seqContainer)
                                {
                                    int currentIndex = targetContainer.Items.Count - 1;  // Item was just added at the end
                                    if (currentIndex > 0)
                                    {
                                        seqContainer.MoveWithinIntoSequenceBlocks(currentIndex, 0);
                                    }
                                }
                                // Track the newly added item and all its descendants
                                createdItemId = TrackItemRecursive(clonedItem);
                            }
                        });
                    }
                    else
                    {
                        // For non-containers, add before/after the item in its parent
                        // First, calculate the index path to get the parent
                        var indexPath = CalculateIndexPathForItem(targetItem);
                        if (string.IsNullOrEmpty(indexPath))
                        {
                            HttpContext.Response.StatusCode = 404;
                            return new ApiResponse
                            {
                                Success = false,
                                Error = "Could not find position of target item in sequence",
                            };
                        }

                        // Parse the index path and find the parent container
                        var indices = indexPath.Split(',').Select(s => int.Parse(s.Trim(), System.Globalization.CultureInfo.InvariantCulture)).ToList();
                        var mainContainer = GetMainContainer();
                        if (mainContainer == null)
                        {
                            HttpContext.Response.StatusCode = 400;
                            return new ApiResponse
                            {
                                Success = false,
                                Error = "No sequence loaded",
                            };
                        }

                        ISequenceContainer parentContainer = mainContainer;
                        for (int i = 0; i < indices.Count - 1; i++)
                        {
                            var idx = indices[i];
                            if (idx < 0 || idx >= parentContainer.Items.Count)
                            {
                                HttpContext.Response.StatusCode = 404;
                                return new ApiResponse
                                {
                                    Success = false,
                                    Error = $"Invalid index path",
                                };
                            }

                            var item = parentContainer.Items[idx];
                            if (item is ISequenceContainer nextContainer)
                            {
                                parentContainer = nextContainer;
                            }
                            else
                            {
                                HttpContext.Response.StatusCode = 400;
                                return new ApiResponse
                                {
                                    Success = false,
                                    Error = "Item at parent path is not a container",
                                };
                            }
                        }

                        // Check if parent is a regular container
                        bool parentIsRegularContainer = parentContainer.GetType().Name.EndsWith("Container");
                        if (!parentIsRegularContainer)
                        {
                            HttpContext.Response.StatusCode = 400;
                            return new ApiResponse
                            {
                                Success = false,
                                Error = $"Cannot add items to parent '{parentContainer.GetType().Name}'. Only regular containers accept new items.",
                            };
                        }

                        // Add the item before or after the target
                        Application.Current.Dispatcher.Invoke(() =>
                        {
                            var clonedItem = CloneSequenceItem(templateItem);
                            if (clonedItem != null)
                            {
                                parentContainer.Add(clonedItem);
                                if (parentContainer is SequenceContainer seqContainer)
                                {
                                    int currentIndex = parentContainer.Items.Count - 1;  // It was just added at the end
                                    int targetIndex = indices[indices.Count - 1] + (shouldInsertAfter ? 1 : 0);  // Insert after or before target
                                    if (targetIndex != currentIndex)
                                    {
                                        seqContainer.MoveWithinIntoSequenceBlocks(currentIndex, targetIndex);
                                    }
                                }
                                // Track the newly added item and all its descendants
                                createdItemId = TrackItemRecursive(clonedItem);
                            }
                        });
                    }

                    HttpContext.Response.StatusCode = 200;
                    return new ApiResponse
                    {
                        Success = true,
                        Error = null,
                        Response = new { id = createdItemId }
                    };
                }
                catch (Exception ex)
                {
                    Logger.Error($"Error adding item: {ex}");
                    HttpContext.Response.StatusCode = 500;
                    return new ApiResponse
                    {
                        Success = false,
                        Error = $"Failed to add item: {ex.Message}",
                    };
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Error in add endpoint: {ex}");
                HttpContext.Response.StatusCode = 500;
                return new ApiResponse
                {
                    Success = false,
                    Error = $"Internal server error: {ex.Message}",
                };
            }
        }

        /// <summary>
        /// <summary>
        /// POST /api/sequence/duplicate - Duplicate any object (item, trigger, or condition) by ID
        /// For items: duplicated item is added after the original in the same container
        /// For triggers/conditions: duplicated item is added to the same parent container
        /// id: ID of the object to duplicate
        /// </summary>
        [Route(HttpVerbs.Post, "/sequence/duplicate")]
        public ApiResponse DuplicateObject([QueryField] string id)
        {
            try
            {
                if (string.IsNullOrEmpty(id))
                {
                    HttpContext.Response.StatusCode = 400;
                    return new ApiResponse
                    {
                        Success = false,
                        Error = "id parameter is required",
                    };
                }

                var sequenceMediator = TouchNStars.Mediators?.Sequence;
                if (sequenceMediator == null || !sequenceMediator.Initialized)
                {
                    HttpContext.Response.StatusCode = 503;
                    return new ApiResponse
                    {
                        Success = false,
                        Error = "Sequence mediator not initialized",
                    };
                }

                try
                {
                    var objType = GetObjectType(id);
                    if (objType == null)
                    {
                        HttpContext.Response.StatusCode = 404;
                        return new ApiResponse
                        {
                            Success = false,
                            Error = $"Object not found with ID: {id}",
                        };
                    }

                    var obj = FindObjectById(id);
                    if (obj == null)
                    {
                        HttpContext.Response.StatusCode = 404;
                        return new ApiResponse
                        {
                            Success = false,
                            Error = "Object not found",
                        };
                    }

                    var mainContainer = GetMainContainer();
                    if (mainContainer == null)
                    {
                        HttpContext.Response.StatusCode = 400;
                        return new ApiResponse
                        {
                            Success = false,
                            Error = "No sequence loaded",
                        };
                    }

                    string duplicatedObjectId = null;

                    if (objType == ObjectType.Item)
                    {
                        // For items, clone and insert after original in same container
                        var itemToDuplicate = obj as ISequenceItem;
                        var indexPath = CalculateIndexPathForItem(itemToDuplicate);
                        if (string.IsNullOrEmpty(indexPath))
                        {
                            HttpContext.Response.StatusCode = 404;
                            return new ApiResponse
                            {
                                Success = false,
                                Error = "Could not find position of item in sequence",
                            };
                        }

                        var indices = indexPath.Split(',').Select(s => int.Parse(s.Trim(), System.Globalization.CultureInfo.InvariantCulture)).ToList();
                        ISequenceContainer parentContainer = mainContainer;
                        for (int i = 0; i < indices.Count - 1; i++)
                        {
                            var idx = indices[i];
                            if (idx < 0 || idx >= parentContainer.Items.Count)
                            {
                                HttpContext.Response.StatusCode = 404;
                                return new ApiResponse { Success = false, Error = "Could not find parent container" };
                            }
                            var item = parentContainer.Items[idx];
                            if (item is ISequenceContainer nextContainer)
                            {
                                parentContainer = nextContainer;
                            }
                            else
                            {
                                HttpContext.Response.StatusCode = 400;
                                return new ApiResponse { Success = false, Error = "Item at parent path is not a container" };
                            }
                        }

                        int itemIndex = indices[indices.Count - 1];
                        Application.Current.Dispatcher.Invoke(() =>
                        {
                            ISequenceItem clonedItem = CloneSequenceItem(itemToDuplicate);
                            if (clonedItem != null)
                            {
                                parentContainer.Add(clonedItem);
                                if (parentContainer is SequenceContainer seqContainer)
                                {
                                    int targetIndex = itemIndex + 1;
                                    int currentIndex = parentContainer.Items.Count - 1;
                                    if (targetIndex < currentIndex)
                                    {
                                        seqContainer.MoveWithinIntoSequenceBlocks(currentIndex, targetIndex);
                                    }
                                }
                                duplicatedObjectId = TrackItemRecursive(clonedItem);
                            }
                        });
                    }
                    else if (objType == ObjectType.Trigger)
                    {
                        // For triggers, find parent container and clone into its Triggers collection
                        var triggerToDuplicate = obj as ISequenceTrigger;
                        var parentItemContainer = FindTriggerContainer(mainContainer, triggerToDuplicate);
                        if (parentItemContainer == null)
                        {
                            HttpContext.Response.StatusCode = 404;
                            return new ApiResponse { Success = false, Error = "Could not find parent container for trigger" };
                        }

                        Application.Current.Dispatcher.Invoke(() =>
                        {
                            var clonedTrigger = triggerToDuplicate.Clone() as ISequenceTrigger;
                            if (clonedTrigger != null && parentItemContainer is SequenceContainer seqContainer)
                            {
                                seqContainer.Triggers.Add(clonedTrigger);
                                clonedTrigger.AttachNewParent(parentItemContainer);

                                // Initialize the trigger to compute derived/dynamic properties
                                try
                                {
                                    var attachMethod = clonedTrigger.GetType().GetMethod("AttachStaticData", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                                    if (attachMethod != null)
                                    {
                                        attachMethod.Invoke(clonedTrigger, null);
                                    }

                                    var initMethod = clonedTrigger.GetType().GetMethod("Initialize", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                                    if (initMethod != null)
                                    {
                                        initMethod.Invoke(clonedTrigger, null);
                                    }
                                }
                                catch (Exception initEx)
                                {
                                    Logger.Warning($"Could not initialize trigger during duplication: {initEx.Message}");
                                }

                                duplicatedObjectId = TrackTrigger(clonedTrigger);
                            }
                        });
                    }
                    else if (objType == ObjectType.Condition)
                    {
                        // For conditions, find parent container and clone into its Conditions collection
                        var conditionToDuplicate = obj as ISequenceCondition;
                        var parentItemContainer = FindConditionContainer(mainContainer, conditionToDuplicate);
                        if (parentItemContainer == null)
                        {
                            HttpContext.Response.StatusCode = 404;
                            return new ApiResponse { Success = false, Error = "Could not find parent container for condition" };
                        }

                        Application.Current.Dispatcher.Invoke(() =>
                        {
                            var clonedCondition = conditionToDuplicate.Clone() as ISequenceCondition;
                            if (clonedCondition != null && parentItemContainer is SequenceContainer seqContainer)
                            {
                                seqContainer.Conditions.Add(clonedCondition);
                                clonedCondition.AttachNewParent(parentItemContainer);

                                // Initialize the condition to compute derived/dynamic properties
                                try
                                {
                                    var attachMethod = clonedCondition.GetType().GetMethod("AttachStaticData", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                                    if (attachMethod != null)
                                    {
                                        attachMethod.Invoke(clonedCondition, null);
                                    }

                                    var initMethod = clonedCondition.GetType().GetMethod("Initialize", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                                    if (initMethod != null)
                                    {
                                        initMethod.Invoke(clonedCondition, null);
                                    }
                                }
                                catch (Exception initEx)
                                {
                                    Logger.Warning($"Could not initialize condition during duplication: {initEx.Message}");
                                }

                                duplicatedObjectId = TrackCondition(clonedCondition);
                            }
                        });
                    }

                    HttpContext.Response.StatusCode = 200;
                    return new ApiResponse
                    {
                        Success = true,
                        Error = null,
                        Response = new { id = duplicatedObjectId }
                    };
                }
                catch (Exception ex)
                {
                    Logger.Error($"Error duplicating object: {ex}");
                    HttpContext.Response.StatusCode = 500;
                    return new ApiResponse { Success = false, Error = ex.Message, StatusCode = 500, Type = "Error" };
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Error in duplicate endpoint: {ex}");
                HttpContext.Response.StatusCode = 500;
                return new ApiResponse { Success = false, Error = ex.Message, StatusCode = 500, Type = "Error" };
            }
        }

        /// <summary>
        /// Internal method to add a trigger to a sequence item by ID
        /// itemId: ID of the item to add trigger to
        /// triggerType: Type name of the trigger to add
        /// </summary>
        private ApiResponse AddTrigger(string targetId, string triggerType, bool? insertAfter = null)
        {
            try
            {
                if (string.IsNullOrEmpty(targetId) || string.IsNullOrEmpty(triggerType))
                {
                    HttpContext.Response.StatusCode = 400;
                    return new ApiResponse
                    {
                        Success = false,
                        Error = "targetId and triggerType parameters are required",
                    };
                }

                var sequenceMediator = TouchNStars.Mediators?.Sequence;
                if (sequenceMediator == null || !sequenceMediator.Initialized)
                {
                    HttpContext.Response.StatusCode = 503;
                    return new ApiResponse
                    {
                        Success = false,
                        Error = "Sequence mediator not initialized",
                    };
                }

                try
                {
                    // targetId may be an existing trigger (insert before/after it) or a container (append)
                    ISequenceContainer container;
                    int? insertIndex = null;

                    var existingTrigger = FindObjectById(targetId) as ISequenceTrigger;
                    if (existingTrigger != null)
                    {
                        var mainContainer = GetMainContainer();
                        if (mainContainer == null)
                        {
                            HttpContext.Response.StatusCode = 400;
                            return new ApiResponse { Success = false, Error = "No sequence loaded" };
                        }
                        container = FindTriggerContainer(mainContainer, existingTrigger);
                        if (container == null)
                        {
                            HttpContext.Response.StatusCode = 404;
                            return new ApiResponse { Success = false, Error = $"Could not find container for trigger ID: {targetId}" };
                        }
                        if (container is SequenceContainer seqCont)
                        {
                            int idx = seqCont.Triggers.IndexOf(existingTrigger);
                            insertIndex = idx + ((insertAfter ?? true) ? 1 : 0);
                        }
                    }
                    else
                    {
                        var item = FindItemById(targetId);
                        if (item == null)
                        {
                            HttpContext.Response.StatusCode = 404;
                            return new ApiResponse
                            {
                                Success = false,
                                Error = $"Object not found with ID: {targetId}",
                            };
                        }

                        // Check if item can have triggers
                        if (!(item is SequenceContainer))
                        {
                            HttpContext.Response.StatusCode = 400;
                            return new ApiResponse
                            {
                                Success = false,
                                Error = "Only sequence containers can have triggers",
                            };
                        }

                        container = item as ISequenceContainer;
                    }

                    // Get factory and find the trigger template
                    var factory = GetFactory();
                    if (factory?.Triggers == null || factory.Triggers.Count == 0)
                    {
                        HttpContext.Response.StatusCode = 503;
                        return new ApiResponse
                        {
                            Success = false,
                            Error = "Triggers not available in factory",
                        };
                    }

                    var templateTrigger = factory.Triggers.FirstOrDefault(t => t.GetType().Name == triggerType || t.GetType().FullName == triggerType);

                    if (templateTrigger == null)
                    {
                        HttpContext.Response.StatusCode = 400;
                        return new ApiResponse
                        {
                            Success = false,
                            Error = $"Trigger type '{triggerType}' not found in factory",
                        };
                    }

                    // Clone the trigger
                    var clonedTrigger = (templateTrigger.Clone() as ISequenceTrigger);
                    if (clonedTrigger == null)
                    {
                        HttpContext.Response.StatusCode = 500;
                        return new ApiResponse
                        {
                            Success = false,
                            Error = $"Failed to clone trigger of type '{triggerType}'",
                        };
                    }

                    // Add trigger to item
                    string createdTriggerId = null;
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        if (container is SequenceContainer seqContainer)
                        {
                            if (insertIndex.HasValue)
                            {
                                int idx = Math.Max(0, Math.Min(insertIndex.Value, seqContainer.Triggers.Count));
                                seqContainer.Triggers.Insert(idx, clonedTrigger);
                            }
                            else
                            {
                                seqContainer.Triggers.Add(clonedTrigger);
                            }

                            // Attach parent reference - this is crucial for many triggers to work properly
                            clonedTrigger.AttachNewParent(container);

                            // Initialize the trigger to compute derived/dynamic properties
                            try
                            {
                                // Try calling AttachStaticData() first (common NINA pattern for post-load initialization)
                                var attachMethod = clonedTrigger.GetType().GetMethod("AttachStaticData", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                                if (attachMethod != null)
                                {
                                    attachMethod.Invoke(clonedTrigger, null);
                                }

                                // Then call Initialize() to compute derived properties
                                var initMethod = clonedTrigger.GetType().GetMethod("Initialize", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                                if (initMethod != null)
                                {
                                    initMethod.Invoke(clonedTrigger, null);
                                }
                            }
                            catch (Exception initEx)
                            {
                                Logger.Warning($"Could not initialize trigger {triggerType}: {initEx.Message}");
                            }
                        }
                        // Track the trigger with ID
                        createdTriggerId = TrackTrigger(clonedTrigger);
                    });

                    HttpContext.Response.StatusCode = 200;
                    return new ApiResponse
                    {
                        Success = true,
                        Error = null,
                        Response = new { id = createdTriggerId }
                    };
                }
                catch (Exception ex)
                {
                    Logger.Error($"Error adding trigger: {ex}");
                    HttpContext.Response.StatusCode = 500;
                    return new ApiResponse
                    {
                        Success = false,
                        Error = $"Failed to add trigger: {ex.Message}",
                    };
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Error in add trigger endpoint: {ex}");
                HttpContext.Response.StatusCode = 500;
                return new ApiResponse
                {
                    Success = false,
                    Error = $"Internal server error: {ex.Message}",
                };
            }
        }

        /// <summary>
        /// POST /api/sequence/remove - Remove any object (item, trigger, or condition) by ID
        /// id: ID of the object to remove
        /// </summary>
        [Route(HttpVerbs.Post, "/sequence/remove")]
        public ApiResponse RemoveObject([QueryField] string id)
        {
            try
            {
                if (string.IsNullOrEmpty(id))
                {
                    HttpContext.Response.StatusCode = 400;
                    return new ApiResponse
                    {
                        Success = false,
                        Error = "id parameter is required",
                    };
                }

                var sequenceMediator = TouchNStars.Mediators?.Sequence;
                if (sequenceMediator == null || !sequenceMediator.Initialized)
                {
                    HttpContext.Response.StatusCode = 503;
                    return new ApiResponse
                    {
                        Success = false,
                        Error = "Sequence mediator not initialized",
                    };
                }

                try
                {
                    var objType = GetObjectType(id);
                    if (objType == null)
                    {
                        HttpContext.Response.StatusCode = 404;
                        return new ApiResponse
                        {
                            Success = false,
                            Error = $"Object not found with ID: {id}",
                        };
                    }

                    var runningRejection = RejectIfRunning(FindObjectById(id));
                    if (runningRejection != null)
                        return runningRejection;

                    var mainContainer = GetMainContainer();
                    if (mainContainer == null)
                    {
                        HttpContext.Response.StatusCode = 400;
                        return new ApiResponse
                        {
                            Success = false,
                            Error = "No sequence loaded",
                        };
                    }

                    bool removed = false;
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        if (objType == ObjectType.Item)
                        {
                            var obj = FindObjectById(id) as ISequenceItem;
                            if (obj != null)
                                removed = RemoveItemRecursive(mainContainer, obj);
                        }
                        else if (objType == ObjectType.Trigger)
                        {
                            var obj = FindObjectById(id) as ISequenceTrigger;
                            if (obj != null)
                                removed = RemoveTriggerRecursive(mainContainer, obj);
                        }
                        else if (objType == ObjectType.Condition)
                        {
                            var obj = FindObjectById(id) as ISequenceCondition;
                            if (obj != null)
                                removed = RemoveConditionRecursive(mainContainer, obj);
                        }
                    });

                    if (!removed)
                    {
                        HttpContext.Response.StatusCode = 404;
                        return new ApiResponse
                        {
                            Success = false,
                            Error = "Object not found in sequence",
                        };
                    }

                    // Remove from tracking
                    UntrackId(id);

                    HttpContext.Response.StatusCode = 200;
                    return new ApiResponse
                    {
                        Success = true,
                        Error = null,

                    };
                }
                catch (Exception ex)
                {
                    Logger.Error($"Error removing object: {ex}");
                    HttpContext.Response.StatusCode = 500;
                    return new ApiResponse
                    {
                        Success = false,
                        Error = $"Failed to remove object: {ex.Message}",
                    };
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Error in remove object endpoint: {ex}");
                HttpContext.Response.StatusCode = 500;
                return new ApiResponse
                {
                    Success = false,
                    Error = $"Internal server error: {ex.Message}",
                };
            }
        }

        /// <summary>
        /// Internal method to add a condition to a sequence item by ID
        /// itemId: ID of the item to add condition to
        /// conditionType: Type name of the condition to add
        /// </summary>
        private ApiResponse AddCondition(string targetId, string conditionType, bool? insertAfter = null)
        {
            try
            {
                if (string.IsNullOrEmpty(targetId) || string.IsNullOrEmpty(conditionType))
                {
                    HttpContext.Response.StatusCode = 400;
                    return new ApiResponse
                    {
                        Success = false,
                        Error = "targetId and conditionType parameters are required",
                    };
                }

                var sequenceMediator = TouchNStars.Mediators?.Sequence;
                if (sequenceMediator == null || !sequenceMediator.Initialized)
                {
                    HttpContext.Response.StatusCode = 503;
                    return new ApiResponse
                    {
                        Success = false,
                        Error = "Sequence mediator not initialized",
                    };
                }

                try
                {
                    // targetId may be an existing condition (insert before/after it) or a container (append)
                    ISequenceContainer container;
                    int? insertIndex = null;

                    var existingCondition = FindObjectById(targetId) as ISequenceCondition;
                    if (existingCondition != null)
                    {
                        var mainContainer = GetMainContainer();
                        if (mainContainer == null)
                        {
                            HttpContext.Response.StatusCode = 400;
                            return new ApiResponse { Success = false, Error = "No sequence loaded" };
                        }
                        container = FindConditionContainer(mainContainer, existingCondition);
                        if (container == null)
                        {
                            HttpContext.Response.StatusCode = 404;
                            return new ApiResponse { Success = false, Error = $"Could not find container for condition ID: {targetId}" };
                        }
                        if (container is SequenceContainer seqCont)
                        {
                            int idx = seqCont.Conditions.IndexOf(existingCondition);
                            insertIndex = idx + ((insertAfter ?? true) ? 1 : 0);
                        }
                    }
                    else
                    {
                        var item = FindItemById(targetId);
                        if (item == null)
                        {
                            HttpContext.Response.StatusCode = 404;
                            return new ApiResponse
                            {
                                Success = false,
                                Error = $"Object not found with ID: {targetId}",
                            };
                        }

                        // Check if item can have conditions
                        if (!(item is ISequenceContainer cont))
                        {
                            HttpContext.Response.StatusCode = 400;
                            return new ApiResponse
                            {
                                Success = false,
                                Error = "Only containers can have conditions",
                            };
                        }

                        container = cont;
                    }

                    // Get factory and find the condition template
                    var factory = GetFactory();
                    if (factory?.Conditions == null || factory.Conditions.Count == 0)
                    {
                        HttpContext.Response.StatusCode = 503;
                        return new ApiResponse
                        {
                            Success = false,
                            Error = "Conditions not available in factory",
                        };
                    }

                    var templateCondition = factory.Conditions.FirstOrDefault(c => c.GetType().Name == conditionType || c.GetType().FullName == conditionType);

                    if (templateCondition == null)
                    {
                        HttpContext.Response.StatusCode = 400;
                        return new ApiResponse
                        {
                            Success = false,
                            Error = $"Condition type '{conditionType}' not found in factory",
                        };
                    }

                    // Clone the condition
                    var clonedCondition = (templateCondition.Clone() as ISequenceCondition);
                    if (clonedCondition == null)
                    {
                        HttpContext.Response.StatusCode = 500;
                        return new ApiResponse
                        {
                            Success = false,
                            Error = $"Failed to clone condition of type '{conditionType}'",
                        };
                    }

                    // Add condition to item
                    string createdConditionId = null;
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        if (container is SequenceContainer seqContainer)
                        {
                            if (insertIndex.HasValue)
                            {
                                int idx = Math.Max(0, Math.Min(insertIndex.Value, seqContainer.Conditions.Count));
                                seqContainer.Conditions.Insert(idx, clonedCondition);
                            }
                            else
                            {
                                seqContainer.Conditions.Add(clonedCondition);
                            }

                            // Attach parent reference - this is crucial for many conditions to work properly
                            clonedCondition.AttachNewParent(container);

                            // Initialize the condition to compute derived/dynamic properties
                            // Many NINA conditions (e.g., MoonAltitudeCondition) need this to calculate
                            // properties like "current altitude" and "time until fulfillment"
                            try
                            {
                                // Try calling AttachStaticData() first (common NINA pattern for post-load initialization)
                                var attachMethod = clonedCondition.GetType().GetMethod("AttachStaticData", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                                if (attachMethod != null)
                                {
                                    attachMethod.Invoke(clonedCondition, null);
                                }

                                // Then call Initialize() to compute derived properties
                                var initMethod = clonedCondition.GetType().GetMethod("Initialize", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                                if (initMethod != null)
                                {
                                    initMethod.Invoke(clonedCondition, null);
                                }
                            }
                            catch (Exception initEx)
                            {
                                Logger.Warning($"Could not initialize condition {conditionType}: {initEx.Message}");
                            }
                        }
                        // Track the condition with ID
                        createdConditionId = TrackCondition(clonedCondition);
                    });

                    HttpContext.Response.StatusCode = 200;
                    return new ApiResponse
                    {
                        Success = true,
                        Error = null,
                        Response = new { id = createdConditionId }
                    };
                }
                catch (Exception ex)
                {
                    Logger.Error($"Error adding condition: {ex}");
                    HttpContext.Response.StatusCode = 500;
                    return new ApiResponse
                    {
                        Success = false,
                        Error = $"Failed to add condition: {ex.Message}",
                    };
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Error in add condition endpoint: {ex}");
                HttpContext.Response.StatusCode = 500;
                return new ApiResponse
                {
                    Success = false,
                    Error = $"Internal server error: {ex.Message}",
                };
            }
        }

        /// <summary>
        /// POST /api/sequence/save - Save the current sequence to a file
        /// filePath: Target file path
        /// </summary>
        [Route(HttpVerbs.Post, "/sequence/save")]
        public async Task<ApiResponse> SaveSequenceToFile([QueryField] string filePath)
        {
            try
            {
                if (string.IsNullOrEmpty(filePath))
                {
                    HttpContext.Response.StatusCode = 400;
                    return new ApiResponse { Success = false, Error = "filePath parameter required", StatusCode = 400, Type = "Error" };
                }

                if (!TryResolveSequenceFile(filePath, out var resolvedPath, out var pathRejection))
                    return pathRejection;
                filePath = resolvedPath;

                var sequenceMediator = TouchNStars.Mediators?.Sequence;
                if (sequenceMediator == null || !sequenceMediator.Initialized)
                {
                    HttpContext.Response.StatusCode = 503;
                    return new ApiResponse { Success = false, Error = "Sequence mediator not initialized", StatusCode = 400, Type = "Error" };
                }

                try
                {
                    var mainContainer = GetMainContainer();

                    if (mainContainer == null)
                    {
                        HttpContext.Response.StatusCode = 400;
                        return new ApiResponse { Success = false, Error = "No sequence loaded", StatusCode = 400, Type = "Error" };
                    }

                    // Ensure directory exists (only subfolders of the sequence folder can get here)
                    var directory = Path.GetDirectoryName(filePath);
                    if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                        Directory.CreateDirectory(directory);

                    // Use mediator to save the sequence
                    using (var cts = new System.Threading.CancellationTokenSource())
                    {
                        await sequenceMediator.SaveContainer(mainContainer, filePath, cts.Token);
                    }

                    HttpContext.Response.StatusCode = 200;
                    return new ApiResponse
                    {
                        Success = true,
                        Error = null,
                        StatusCode = 200,
                        Type = "Success"
                    };
                }
                catch (Exception ex)
                {
                    Logger.Error($"Error saving sequence: {ex}");
                    HttpContext.Response.StatusCode = 500;
                    return new ApiResponse { Success = false, Error = ex.Message, StatusCode = 400, Type = "Error" };
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Error in save endpoint: {ex}");
                HttpContext.Response.StatusCode = 500;
                return new ApiResponse { Success = false, Error = ex.Message, StatusCode = 400, Type = "Error" };
            }
        }

        /// <summary>
        /// DELETE /api/sequence/delete - Delete a sequence file
        /// filePath: Path of the file to delete
        /// </summary>
        [Route(HttpVerbs.Delete, "/sequence/delete")]
        public ApiResponse DeleteSequenceFile([QueryField] string filePath)
        {
            try
            {
                if (string.IsNullOrEmpty(filePath))
                {
                    HttpContext.Response.StatusCode = 400;
                    return new ApiResponse { Success = false, Error = "filePath parameter required", StatusCode = 400, Type = "Error" };
                }

                if (!TryResolveSequenceFile(filePath, out var resolvedPath, out var pathRejection))
                    return pathRejection;
                filePath = resolvedPath;

                if (!File.Exists(filePath))
                {
                    HttpContext.Response.StatusCode = 404;
                    return new ApiResponse { Success = false, Error = $"File not found: {filePath}", StatusCode = 404, Type = "Error" };
                }

                File.Delete(filePath);

                HttpContext.Response.StatusCode = 200;
                return new ApiResponse { Success = true, Error = null, StatusCode = 200, Type = "Success" };
            }
            catch (Exception ex)
            {
                Logger.Error($"Error deleting sequence file: {ex}");
                HttpContext.Response.StatusCode = 500;
                return new ApiResponse { Success = false, Error = ex.Message, StatusCode = 500, Type = "Error" };
            }
        }

        /// <summary>
        /// GET /api/sequence/info - Get detailed metadata about any object (item, trigger, or condition) by ID
        /// id: ID of the object (item, trigger, or condition)
        /// Returns: Object as hashtable with properties directly embedded (same pattern as getSequenceRecursively)
        /// </summary>
        [Route(HttpVerbs.Get, "/sequence/info")]
        public object GetPropertyInfo([QueryField] string id)
        {
            try
            {
                if (string.IsNullOrEmpty(id))
                {
                    HttpContext.Response.StatusCode = 400;
                    return new
                    {
                        Success = false,
                        Error = "id required",
                        StatusCode = 400,
                        Type = "Error"
                    };
                }

                // Determine object type (item, trigger, or condition)
                var objType = GetObjectType(id);
                if (objType == null)
                {
                    HttpContext.Response.StatusCode = 404;
                    return new
                    {
                        Success = false,
                        Error = $"Object not found with ID: {id}",
                        StatusCode = 404,
                        Type = "Error"
                    };
                }

                var obj = FindObjectById(id);
                if (obj == null)
                {
                    HttpContext.Response.StatusCode = 404;
                    return new
                    {
                        Success = false,
                        Error = "Object not found",
                        StatusCode = 404,
                        Type = "Error"
                    };
                }

                // Build hashtable with object info (same pattern as getSequenceRecursively)
                var objectInfo = new Hashtable
                {
                    { "Id", id },
                    { "Name", ((dynamic)obj).Name },
                    { "Status", ((dynamic)obj).Status.ToString() },
                    { "FullTypeName", obj.GetType().FullName }
                };

                // Add all public properties directly to hashtable (same pattern as getSequenceRecursively)
                var objTypeInfo = obj.GetType();

                // Determine which base class to exclude properties from
                var basePropertiesToExclude = Array.Empty<PropertyInfo>();
                if (obj is ISequenceItem)
                    basePropertiesToExclude = typeof(SequenceItem).GetProperties();
                else if (obj is ISequenceTrigger)
                    basePropertiesToExclude = typeof(SequenceTrigger).GetProperties();
                else if (obj is ISequenceCondition)
                    basePropertiesToExclude = typeof(SequenceCondition).GetProperties();

                // Check if this is a structural container - if so, exclude Items to avoid bloat
                // Structural containers: SequentialContainer, ParallelContainer, TargetContainer, StartAreaContainer, TargetAreaContainer, EndAreaContainer
                bool isStructuralContainer = obj is ISequenceContainer &&
                    new[] { "SequentialContainer", "ParallelContainer", "DeepSkyObjectContainer", "TargetContainer", "StartAreaContainer", "TargetAreaContainer", "EndAreaContainer" }
                        .Contains(obj.GetType().Name);

                var propertiesNotToShow = isStructuralContainer ? new[] { "Items" } : Array.Empty<string>();

                var proper = objTypeInfo.GetProperties().Where(p =>
                    p.MemberType == MemberTypes.Property &&
                    !ignoredProperties.Contains(p.Name) &&
                    !propertiesNotToShow.Contains(p.Name) &&
                    !basePropertiesToExclude.Any(x => x.Name == p.Name));

                foreach (var prop in proper)
                {
                    if ((prop.GetSetMethod(true)?.IsPublic ?? false) && prop.CanRead && (prop.GetGetMethod(true)?.IsPublic ?? false))
                    {
                        try
                        {
                            objectInfo[prop.Name] = SafeSerializeValue(prop.GetValue(obj));
                        }
                        catch (Exception ex)
                        {
                            Logger.Warning($"Failed to read property {prop.Name}: {ex.Message}");
                        }
                    }
                }

                // Expand WaitLoopData progress fields for altitude-based items/conditions (excluded from JSON opt-in serialization)
                try
                {
                    var dataProperty = objTypeInfo.GetProperty("Data", BindingFlags.Public | BindingFlags.Instance);
                    if (dataProperty != null && dataProperty.CanRead)
                    {
                        var dataValue = dataProperty.GetValue(obj);
                        if (dataValue != null)
                        {
                            var dataType = dataValue.GetType();
                            // Check if Data object has the progress field properties
                            var currentAltProp = dataType.GetProperty("CurrentAltitude", BindingFlags.Public | BindingFlags.Instance);
                            var targetAltProp = dataType.GetProperty("TargetAltitude", BindingFlags.Public | BindingFlags.Instance);
                            var expectedTimeProp = dataType.GetProperty("ExpectedTime", BindingFlags.Public | BindingFlags.Instance);
                            var comparatorProp = dataType.GetProperty("Comparator", BindingFlags.Public | BindingFlags.Instance);

                            if (currentAltProp != null && targetAltProp != null && expectedTimeProp != null && comparatorProp != null)
                            {
                                objectInfo["CurrentAltitude"] = currentAltProp.GetValue(dataValue);
                                objectInfo["TargetAltitude"] = targetAltProp.GetValue(dataValue);
                                objectInfo["ExpectedTime"] = expectedTimeProp.GetValue(dataValue);
                                objectInfo["Comparator"] = comparatorProp.GetValue(dataValue)?.ToString();
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warning($"Could not extract progress fields from Data property: {ex.Message}");
                }

                // TimeSpanCondition/TimeCondition - RemainingTime is read-only (no public setter)
                if (obj is TimeSpanCondition timeSpanCondObj)
                {
                    objectInfo["RemainingTime"] = timeSpanCondObj.RemainingTime.ToString(@"hh\:mm\:ss");
                }
                else if (obj is TimeCondition timeCondObj)
                {
                    objectInfo["RemainingTime"] = timeCondObj.RemainingTime.ToString(@"hh\:mm\:ss");
                }

                // SafetyMonitorCondition - IsSafe has a protected setter
                if (obj is SafetyMonitorCondition safetyCondObj)
                {
                    objectInfo["IsSafe"] = safetyCondObj.IsSafe;
                }

                // For non-structural container items with subitems, show the Items formatted nicely (e.g., SmartExposure, Focus, etc.)
                if (!isStructuralContainer && obj is ISequenceContainer featureContainer)
                {
                    objectInfo.Add("Items", RetryOnConcurrentModification(() => getSequenceRecursively(featureContainer)));
                }

                // For all containers, include triggers and conditions
                if (obj is ISequenceContainer container)
                {
                    var seqContainer = obj as SequenceContainer;
                    if (seqContainer != null)
                    {
                        objectInfo.Add("Triggers", RetryOnConcurrentModification(() => getTriggers(seqContainer)));
                        objectInfo.Add("Conditions", RetryOnConcurrentModification(() => getConditions(seqContainer)));
                    }
                }

                HttpContext.Response.StatusCode = 200;
                WriteSequenceResponseData(HttpContext, objectInfo);

                return null;
            }
            catch (Exception ex)
            {
                Logger.Error($"Error getting object info: {ex}");
                HttpContext.Response.StatusCode = 500;
                return new
                {
                    Success = false,
                    Error = ex.Message,
                    StatusCode = 500,
                    Type = "Error"
                };
            }
        }

        /// <summary>
        /// <summary>
        /// POST /api/sequence/set - Set a property value on any object (item, trigger, or condition) by ID
        /// id: ID of the object
        /// propertyName: Name of the property to set (supports nested properties with dot notation, e.g., "Target.PositionAngle")
        /// value: New value (as string, will be converted to proper type)
        /// </summary>
        [Route(HttpVerbs.Post, "/sequence/set")]
        public ApiResponse SetObjectProperty([QueryField] string id, [QueryField] string propertyName, [QueryField] string value)
        {
            try
            {
                if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(propertyName))
                {
                    HttpContext.Response.StatusCode = 400;
                    return new ApiResponse { Success = false, Error = "id and propertyName required", StatusCode = 400, Type = "Error" };
                }

                var obj = FindObjectById(id);
                if (obj == null)
                {
                    HttpContext.Response.StatusCode = 404;
                    return new ApiResponse { Success = false, Error = "Object not found", StatusCode = 404, Type = "Error" };
                }

                var runningRejection = RejectIfRunning(obj);
                if (runningRejection != null)
                    return runningRejection;

                try
                {
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        // Handle flat aliases for WaitLoopData fields (mirrors GetPropertyInfo flattening).
                        // "TargetAltitude" -> "Data.Offset"  (the persistent user-settable value; Data.TargetAltitude
                        //   is computed and gets overwritten by CalculateExpectedTime on every Check() call).
                        // "Comparator"     -> "Data.Comparator"
                        if (propertyName == "TargetAltitude" || propertyName == "Comparator")
                        {
                            var dataPropCheck = obj.GetType().GetProperty("Data", BindingFlags.Public | BindingFlags.Instance);
                            if (dataPropCheck != null)
                            {
                                var dataValCheck = dataPropCheck.GetValue(obj);
                                if (dataValCheck != null)
                                {
                                    var dataTCheck = dataValCheck.GetType();
                                    if (propertyName == "TargetAltitude" &&
                                        dataTCheck.GetProperty("Offset", BindingFlags.Public | BindingFlags.Instance)?.GetSetMethod() != null)
                                    {
                                        propertyName = "Data.Offset";
                                    }
                                    else if (propertyName == "Comparator" &&
                                        dataTCheck.GetProperty("Comparator", BindingFlags.Public | BindingFlags.Instance)?.GetSetMethod() != null)
                                    {
                                        propertyName = "Data.Comparator";
                                    }
                                }
                            }
                        }

                        // Support nested properties (e.g., "Target.PositionAngle")
                        // Also supports indexed collection access (e.g., "ExposureItems[0].Filter")
                        var propertyParts = propertyName.Split('.');

                        object currentObj = obj;
                        Type currentType = obj.GetType();
                        object rootObj = obj;

                        // Navigate through nested properties
                        for (int i = 0; i < propertyParts.Length - 1; i++)
                        {
                            var currentPropName = propertyParts[i];

                            // Check for index notation e.g. ExposureItems[2]
                            int? collectionIndex = null;
                            var bracketStart = currentPropName.IndexOf('[');
                            if (bracketStart >= 0)
                            {
                                var bracketEnd = currentPropName.IndexOf(']', bracketStart);
                                if (bracketEnd > bracketStart &&
                                    int.TryParse(currentPropName.Substring(bracketStart + 1, bracketEnd - bracketStart - 1), out int idx))
                                {
                                    collectionIndex = idx;
                                    currentPropName = currentPropName.Substring(0, bracketStart);
                                }
                            }

                            var currentProp = currentType.GetProperty(currentPropName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);

                            if (currentProp == null)
                            {
                                throw new Exception($"Property '{currentPropName}' not found on type '{currentType.Name}'");
                            }

                            currentObj = currentProp.GetValue(currentObj);
                            if (currentObj == null)
                            {
                                throw new Exception($"Property '{currentPropName}' returned null; cannot navigate further through '{propertyName}'");
                            }

                            // If an index was specified, dereference the collection
                            if (collectionIndex.HasValue)
                            {
                                if (currentObj is System.Collections.IList list)
                                {
                                    if (collectionIndex.Value < 0 || collectionIndex.Value >= list.Count)
                                        throw new Exception($"Index {collectionIndex.Value} is out of range for '{currentPropName}' (count: {list.Count})");
                                    currentObj = list[collectionIndex.Value];
                                }
                                else
                                {
                                    throw new Exception($"Property '{currentPropName}' does not implement IList and cannot be indexed");
                                }
                            }

                            currentType = currentObj.GetType();
                        }

                        // Get the final property to set
                        var finalPropName = propertyParts[propertyParts.Length - 1];
                        var finalProp = currentType.GetProperty(finalPropName, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);

                        if (finalProp == null)
                        {
                            throw new Exception($"Property '{finalPropName}' not found on type '{currentType.Name}' (full path: {propertyName})");
                        }

                        // Check if property has a public setter
                        var setMethod = finalProp.GetSetMethod();
                        if (setMethod == null)
                        {
                            throw new Exception($"Property '{finalPropName}' does not have a public setter (may be read-only)");
                        }

                        // String properties backed by a list of valid values (e.g. SelectedMode + Modes)
                        // silently accept anything - reject values outside that list instead.
                        if (finalProp.PropertyType == typeof(string))
                        {
                            var options = FindOptionList(currentObj, finalProp);
                            if (options != null && !options.Contains(value))
                            {
                                throw new Exception($"'{value}' is not a valid value for '{finalPropName}'. Valid values: {string.Join(", ", options)}");
                            }
                        }

                        // Convert and set the value
                        object convertedValue = ConvertValue(value, finalProp.PropertyType);
                        finalProp.SetValue(currentObj, convertedValue);

                        // Some properties ignore the setter (e.g. values mirrored from the profile).
                        // Read simple values back so the client learns that nothing changed.
                        if (IsSimpleEditableType(finalProp.PropertyType))
                        {
                            var actual = finalProp.GetValue(currentObj);
                            if (!SimpleValuesEqual(actual, convertedValue))
                            {
                                throw new Exception($"'{finalPropName}' did not accept the value, it is still '{actual}'");
                            }
                        }

                        // Manually raise PropertyChanged notification if the object supports it
                        // This handles properties that don't raise notifications themselves (NINA bug workaround)
                        var raiseMethod = currentObj.GetType().GetMethod("RaisePropertyChanged",
                            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                            null,
                            new[] { typeof(string) },
                            null);
                        if (raiseMethod != null)
                        {
                            try
                            {
                                raiseMethod.Invoke(currentObj, new object[] { finalPropName });
                            }
                            catch (Exception ex)
                            {
                                Logger.Warning($"Failed to raise PropertyChanged for {finalPropName}: {ex.Message}");
                            }
                        }

                        Logger.Debug($"Property '{propertyName}' set to '{value}' on type '{obj.GetType().Name}'");
                    });

                    HttpContext.Response.StatusCode = 200;
                    return new ApiResponse
                    {
                        Success = true,
                        Error = null,
                    };
                }
                catch (TargetInvocationException ex)
                {
                    // Unwrap TargetInvocationException to show the actual error
                    var innerException = ex.InnerException ?? ex;
                    Logger.Error($"Error setting property {propertyName}: {innerException.Message}");
                    HttpContext.Response.StatusCode = 400;
                    return new ApiResponse { Success = false, Error = $"Failed to set property: {innerException.Message}", StatusCode = 400, Type = "Error" };
                }
                catch (Exception ex)
                {
                    Logger.Error($"Error setting property {propertyName}: {ex.Message}");
                    HttpContext.Response.StatusCode = 400;
                    return new ApiResponse { Success = false, Error = $"Failed to set property: {ex.Message}", StatusCode = 400, Type = "Error" };
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Error in set property endpoint: {ex}");
                HttpContext.Response.StatusCode = 500;
                return new ApiResponse { Success = false, Error = ex.Message, StatusCode = 500, Type = "Error" };
            }
        }

        private ISequenceItem CloneSequenceItem(ISequenceItem source)
        {
            try
            {
                Logger.Info($"DEBUG CloneSequenceItem: Source type={source.GetType().Name}");

                if (source is ISequenceContainer container)
                {
                    var triggersCount = (container as SequenceContainer)?.Triggers.Count ?? 0;
                    var conditionsCount = (container as SequenceContainer)?.Conditions.Count ?? 0;
                    Logger.Info($"  Container has {container.Items.Count} items, {triggersCount} triggers, {conditionsCount} conditions");
                }

                // For most items, use the built-in Clone() which handles code-generated patterns
                // But we need to ensure complex items like SmartExposure with nested triggers/conditions work
                ISequenceItem cloned = source.Clone() as ISequenceItem;

                if (cloned == null)
                {
                    Logger.Error($"Clone() returned null for {source.GetType().Name}");
                    throw new Exception($"Unable to clone {source.GetType().Name}");
                }

                if (cloned is ISequenceContainer clonedContainer)
                {
                    var triggersCount = (cloned as SequenceContainer)?.Triggers.Count ?? 0;
                    var conditionsCount = (cloned as SequenceContainer)?.Conditions.Count ?? 0;
                    Logger.Info($"  Cloned container has {clonedContainer.Items.Count} items, {triggersCount} triggers, {conditionsCount} conditions");
                }

                // Ensure parent references are set correctly for all nested items
                if (source is ISequenceContainer sourceContainer && cloned is ISequenceContainer clonedContainer2)
                {
                    foreach (var item in clonedContainer2.Items)
                    {
                        item.AttachNewParent(clonedContainer2);
                    }

                    // Attach parent for triggers and conditions if they exist
                    var seqContainer = cloned as SequenceContainer;
                    if (seqContainer != null)
                    {
                        foreach (var trigger in seqContainer.Triggers)
                        {
                            trigger.AttachNewParent(clonedContainer2);
                        }

                        foreach (var condition in seqContainer.Conditions)
                        {
                            condition.AttachNewParent(clonedContainer2);
                        }
                    }
                }

                return cloned;
            }
            catch (Exception ex)
            {
                Logger.Error($"Error cloning item: {ex}");
                throw;
            }
        }

        /// <summary>
        /// GET /api/sequence/fields?id= - Describes the directly editable properties of an item,
        /// trigger or condition so a generic editor does not have to guess types from JSON values.
        /// Each field: { Name, Type, Options?, ReadOnly }. Type is one of integer, number, boolean,
        /// string, choice, guid, timespan, datetime. Complex properties are left out.
        /// </summary>
        [Route(HttpVerbs.Get, "/sequence/fields")]
        public object GetEditableFields([QueryField] string id)
        {
            try
            {
                var obj = FindObjectById(id);
                if (obj == null)
                {
                    HttpContext.Response.StatusCode = 404;
                    return new ApiResponse { Success = false, Error = "Object not found", StatusCode = 404, Type = "Error" };
                }

                var fields = Application.Current.Dispatcher.Invoke(() => DescribeEditableFields(obj));

                HttpContext.Response.StatusCode = 200;
                WriteSequenceResponseData(HttpContext, new Hashtable
                {
                    { "Success", true },
                    { "Fields", fields }
                });
                return null;
            }
            catch (Exception ex)
            {
                Logger.Error($"Error describing sequence fields: {ex}");
                HttpContext.Response.StatusCode = 500;
                return new ApiResponse { Success = false, Error = ex.Message, StatusCode = 500, Type = "Error" };
            }
        }

        private static List<Hashtable> DescribeEditableFields(object obj)
        {
            // Same property selection as the tree serializer: own public get/set properties only
            Type baseType = obj is ISequenceTrigger ? typeof(SequenceTrigger)
                : obj is ISequenceCondition ? typeof(SequenceCondition)
                : typeof(SequenceItem);
            var baseNames = new HashSet<string>(baseType.GetProperties().Select(p => p.Name));

            var result = new List<Hashtable>();
            foreach (var prop in obj.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (ignoredProperties.Contains(prop.Name) || baseNames.Contains(prop.Name)) continue;
                if (prop.GetIndexParameters().Length > 0) continue;
                if (!(prop.GetGetMethod()?.IsPublic ?? false) || !(prop.GetSetMethod()?.IsPublic ?? false)) continue;

                var type = Nullable.GetUnderlyingType(prop.PropertyType) ?? prop.PropertyType;
                string kind;
                List<string> options = null;
                if (type == typeof(bool)) kind = "boolean";
                else if (type == typeof(int) || type == typeof(long) || type == typeof(short) || type == typeof(byte)) kind = "integer";
                else if (type == typeof(double) || type == typeof(float) || type == typeof(decimal)) kind = "number";
                else if (type == typeof(Guid)) kind = "guid";
                else if (type == typeof(TimeSpan)) kind = "timespan";
                else if (type == typeof(DateTime)) kind = "datetime";
                else if (type.IsEnum)
                {
                    kind = "choice";
                    options = Enum.GetNames(type).ToList();
                }
                else if (type == typeof(string))
                {
                    options = FindOptionList(obj, prop);
                    kind = options != null ? "choice" : "string";
                }
                else continue;

                var field = new Hashtable
                {
                    { "Name", prop.Name },
                    { "Type", kind },
                    { "ReadOnly", IsRuntimeStateProperty(prop.Name) }
                };
                if (options != null) field["Options"] = options;
                result.Add(field);
            }
            return result;
        }

        /// <summary>
        /// Progress and state properties that are publicly settable but owned by the item while
        /// it runs (counters, display text). Editing them only corrupts the item's bookkeeping.
        /// </summary>
        private static bool IsRuntimeStateProperty(string name)
        {
            return name.StartsWith("Completed") || name.StartsWith("Total") ||
                   name.EndsWith("WasCompleted") || name == "DisplayText";
        }

        /// <summary>
        /// Finds the list of valid values for a string property by the naming conventions NINA
        /// and its plugins use: SelectedMode -> Modes, BinningMode -> BinningModeChoices, ...
        /// </summary>
        private static List<string> FindOptionList(object owner, PropertyInfo prop)
        {
            var baseName = prop.Name.StartsWith("Selected") && prop.Name.Length > 8 ? prop.Name.Substring(8) : prop.Name;
            var candidates = new[]
            {
                baseName + "s", baseName + "Choices", baseName + "Options", baseName + "List",
                "Available" + baseName + "s", prop.Name + "s", prop.Name + "Choices", prop.Name + "Options"
            };
            foreach (var name in candidates.Distinct())
            {
                if (name == prop.Name) continue;
                var listProp = owner.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                if (listProp == null || listProp.GetIndexParameters().Length > 0) continue;
                if (listProp.PropertyType == typeof(string) || !typeof(IEnumerable).IsAssignableFrom(listProp.PropertyType)) continue;
                try
                {
                    if (listProp.GetValue(owner) is IEnumerable values)
                    {
                        var list = values.Cast<object>().Where(v => v != null).Select(v => v.ToString()).ToList();
                        if (list.Count > 0) return list;
                    }
                }
                catch { /* try the next candidate */ }
            }
            return null;
        }

        private static bool IsSimpleEditableType(Type type)
        {
            type = Nullable.GetUnderlyingType(type) ?? type;
            return type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(decimal) ||
                   type == typeof(TimeSpan) || type == typeof(DateTime) || type == typeof(Guid);
        }

        private static bool SimpleValuesEqual(object actual, object expected)
        {
            // NaN never compares equal to itself, but writing NaN back is not a rejection
            if (actual is double a && expected is double e) return (double.IsNaN(a) && double.IsNaN(e)) || Math.Abs(a - e) < 1e-9;
            if (actual is float af && expected is float ef) return (float.IsNaN(af) && float.IsNaN(ef)) || Math.Abs(af - ef) < 1e-6f;
            return Equals(actual, expected);
        }

        /// <summary>
        /// Helper method to convert string value to proper type
        /// </summary>
        private object ConvertValue(string value, Type targetType)
        {
            if (value == null)
                return targetType.IsValueType ? Activator.CreateInstance(targetType) : null;

            try
            {
                if (targetType == typeof(string))
                    return value;

                if (targetType == typeof(bool))
                    return bool.Parse(value);

                if (targetType == typeof(int))
                    return int.Parse(value, System.Globalization.CultureInfo.InvariantCulture);

                // NINA's UI rejects these through its validation rules; NaN or Infinity as an
                // exposure time or position would be handed straight to the device drivers.
                if (targetType == typeof(double))
                {
                    var d = double.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
                    if (!double.IsFinite(d))
                        throw new FormatException($"'{value}' is not a finite number");
                    return d;
                }

                if (targetType == typeof(decimal))
                    return decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture);

                if (targetType == typeof(long))
                    return long.Parse(value, System.Globalization.CultureInfo.InvariantCulture);

                if (targetType == typeof(float))
                {
                    var f = float.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
                    if (!float.IsFinite(f))
                        throw new FormatException($"'{value}' is not a finite number");
                    return f;
                }

                if (targetType.IsEnum)
                {
                    // Enum.Parse also accepts any number ("999"), which no switch in NINA expects
                    var parsed = Enum.Parse(targetType, value);
                    if (!targetType.IsDefined(typeof(FlagsAttribute), false) && !Enum.IsDefined(targetType, parsed))
                        throw new FormatException($"'{value}' is not a valid value. Valid values: {string.Join(", ", Enum.GetNames(targetType))}");
                    return parsed;
                }

                if (targetType == typeof(TimeSpan))
                    return TimeSpan.Parse(value, System.Globalization.CultureInfo.InvariantCulture);

                if (targetType == typeof(DateTime))
                    return DateTime.Parse(value, System.Globalization.CultureInfo.InvariantCulture);

                if (targetType == typeof(Guid))
                    return Guid.Parse(value);

                // Special handling for FilterInfo - look up by name
                if (targetType.Name == "FilterInfo")
                {
                    try
                    {
                        // Allow explicit null to use current filter
                        if (value.Equals("null", StringComparison.OrdinalIgnoreCase))
                            return null;

                        var profile = TouchNStars.Mediators?.Profile?.ActiveProfile;
                        if (profile?.FilterWheelSettings?.FilterWheelFilters != null)
                        {
                            // Try to find filter by name
                            var filter = profile.FilterWheelSettings.FilterWheelFilters.FirstOrDefault(f => f.Name == value);
                            if (filter != null)
                                return filter;
                        }
                        throw new Exception($"Filter '{value}' not found in active profile");
                    }
                    catch (Exception ex)
                    {
                        Logger.Error($"Error looking up filter by name '{value}': {ex.Message}");
                        throw;
                    }
                }

                // Special handling for IDateTimeProvider - look up by full type name or display name
                if (typeof(NINA.Sequencer.Utility.DateTimeProvider.IDateTimeProvider).IsAssignableFrom(targetType))
                {
                    var factory = GetFactory();
                    if (factory?.DateTimeProviders != null)
                    {
                        var provider = factory.DateTimeProviders.FirstOrDefault(p =>
                            string.Equals(p.GetType().FullName, value, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(p.GetType().Name, value, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(p.Name, value, StringComparison.OrdinalIgnoreCase));
                        if (provider != null)
                            return provider;
                    }
                    throw new Exception($"DateTimeProvider '{value}' not found");
                }

                // For complex types, try JSON deserialization
                return Newtonsoft.Json.JsonConvert.DeserializeObject(value, targetType);
            }
            catch (Exception ex)
            {
                Logger.Error($"Error converting value '{value}' to type {targetType.Name}: {ex}");
                throw;
            }
        }

        // Limits for serializing arbitrary (plugin) property values. Plugin objects can hold
        // back-references to their parents, view models or huge lists; without these limits a
        // single self-referencing object overflows the stack and takes the whole NINA process down.
        private const int MaxSerializeDepth = 4;
        private const int MaxCollectionItems = 50;

        /// <summary>
        /// Helper method to safely serialize a value to something JSON-serializable
        /// </summary>
        private static object SafeSerializeValue(object value)
        {
            return SafeSerializeValue(value, 0, new HashSet<object>(ReferenceEqualityComparer.Instance));
        }

        private static object SafeSerializeValue(object value, int depth, HashSet<object> visited)
        {
            if (value == null)
                return null;

            // Handle primitives and common types
            var type = value.GetType();
            if (type.IsPrimitive || type == typeof(string) || type == typeof(decimal) ||
                type == typeof(DateTime) || type == typeof(DateTimeOffset) || type == typeof(TimeSpan) || type == typeof(Guid))
            {
                return value;
            }

            // Handle enums
            if (type.IsEnum)
                return value.ToString();

            // Handle IDateTimeProvider - serialize as {Name, FullTypeName}
            if (value is NINA.Sequencer.Utility.DateTimeProvider.IDateTimeProvider dtProvider)
            {
                return new Hashtable
                {
                    { "Name", dtProvider.Name },
                    { "FullTypeName", dtProvider.GetType().FullName }
                };
            }

            // Sequence entities referenced from a property (parents, child containers, ...) are
            // part of the tree anyway - only emit a reference instead of recursing into them.
            if (value is ISequenceEntity entity)
            {
                return new Hashtable
                {
                    { "Name", entity.Name },
                    { "FullTypeName", type.FullName }
                };
            }

            if (IsNonSerializableType(type))
                return null;

            if (depth >= MaxSerializeDepth)
                return SafeToString(value);

            // Reference types may point back to an object that is already being serialized
            if (!type.IsValueType && !visited.Add(value))
                return null;

            try
            {
                return SerializeComplexValue(value, type, depth, visited);
            }
            finally
            {
                if (!type.IsValueType)
                    visited.Remove(value);
            }
        }

        private static object SerializeComplexValue(object value, Type type, int depth, HashSet<object> visited)
        {
            // Handle collections/IEnumerable (convert to array, capped)
            if (value is IEnumerable enumerable)
            {
                try
                {
                    var list = new List<object>();
                    int count = 0;
                    foreach (var item in enumerable)
                    {
                        count++;
                        if (count > MaxCollectionItems)
                        {
                            return new Hashtable
                            {
                                { "_truncated", true },
                                { "Count", (value as ICollection)?.Count ?? count },
                                { "Items", list }
                            };
                        }
                        list.Add(SafeSerializeValue(item, depth + 1, visited));
                    }
                    return list;
                }
                catch { /* Fall through to other handlers */ }
            }

            // Special handling for Expression objects
            if (type.Name == "Expression")
            {
                // Try multiple properties to extract the actual expression value
                var props = new[] { "ExpressionString", "RawExpression", "Definition", "Expression" };
                foreach (var propName in props)
                {
                    try
                    {
                        var propValue = type.GetProperty(propName)?.GetValue(value) as string;
                        if (!string.IsNullOrEmpty(propValue))
                            return propValue;
                    }
                    catch { /* try the next candidate */ }
                }

                // Try to get the Value property
                try
                {
                    var exprValue = type.GetProperty("Value")?.GetValue(value);
                    if (exprValue != null && !(exprValue is string str && str.StartsWith("Undefined")))
                        return SafeSerializeValue(exprValue, depth + 1, visited);
                }
                catch { /* fall back to ToString */ }

                // Return it as-is; if it's "Undefined in  (with Validator)" that reflects the actual state
                return SafeToString(value);
            }

            // Generic fallback for complex types: reflect over [JsonProperty]-annotated properties.
            // This handles InputCoordinates, InputTopocentricCoordinates, InputTarget, and any
            // future NINA type without needing explicit case-by-case handling here.
            var jsonProps = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.CanRead
                         && p.GetIndexParameters().Length == 0
                         && p.GetCustomAttribute<JsonPropertyAttribute>() != null)
                .ToList();

            if (jsonProps.Count > 0)
            {
                var dict = new Hashtable();
                foreach (var prop in jsonProps)
                {
                    try { dict[prop.Name] = SafeSerializeValue(prop.GetValue(value), depth + 1, visited); }
                    catch { /* skip properties that throw */ }
                }
                return dict;
            }

            // Fallback: return string representation for unknown complex types
            return SafeToString(value);
        }

        /// <summary>
        /// Types that never carry user-facing sequence data: commands, delegates, services,
        /// view models, WPF objects, streams. They are dropped instead of serialized.
        /// </summary>
        private static bool IsNonSerializableType(Type type)
        {
            if (typeof(ICommand).IsAssignableFrom(type) ||
                typeof(Delegate).IsAssignableFrom(type) ||
                typeof(Type).IsAssignableFrom(type) ||
                typeof(Stream).IsAssignableFrom(type) ||
                typeof(System.Windows.Threading.DispatcherObject).IsAssignableFrom(type) ||
                typeof(NINA.Profile.Interfaces.IProfileService).IsAssignableFrom(type) ||
                typeof(Task).IsAssignableFrom(type) ||
                typeof(System.Threading.CancellationTokenSource).IsAssignableFrom(type))
            {
                return true;
            }

            var name = type.Name;
            return name.EndsWith("VM") || name.EndsWith("ViewModel") || name.EndsWith("Mediator") ||
                   name.EndsWith("Service") || name.EndsWith("Factory");
        }

        private static string SafeToString(object value)
        {
            try
            {
                return value.ToString();
            }
            catch
            {
                return value.GetType().Name;
            }
        }

        /// <summary>
        /// <summary>
        /// <summary>
        /// POST /api/sequence/enable - Enable or disable any object (items, containers, triggers, or conditions) by ID
        /// Supports all sequence items including Sequential, Parallel, and Target containers
        /// id: ID of the object
        /// enabled: true to enable, false to disable
        /// </summary>
        [Route(HttpVerbs.Post, "/sequence/enable")]
        public ApiResponse SetObjectEnabled([QueryField] string id, [QueryField] bool enabled)
        {
            try
            {
                if (string.IsNullOrEmpty(id))
                {
                    HttpContext.Response.StatusCode = 400;
                    return new ApiResponse { Success = false, Error = "id required", StatusCode = 400, Type = "Error" };
                }

                var obj = FindObjectById(id);
                if (obj == null)
                {
                    HttpContext.Response.StatusCode = 404;
                    return new ApiResponse { Success = false, Error = "Object not found", StatusCode = 404, Type = "Error" };
                }

                var runningRejection = RejectIfRunning(obj);
                if (runningRejection != null)
                    return runningRejection;

                try
                {
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        // Use the Status property: DISABLED when disabled, CREATED when enabled
                        // Works for all sequence items including containers (Sequential, Parallel, Target)
                        var statusProp = obj.GetType().GetProperty("Status", BindingFlags.IgnoreCase | BindingFlags.Public | BindingFlags.Instance);
                        if (statusProp != null && statusProp.CanWrite)
                        {
                            // Find the SequenceEntityStatus enum values
                            var statusType = statusProp.PropertyType;
                            if (statusType.IsEnum)
                            {
                                // Get the DISABLED and CREATED enum values
                                object targetStatus = null;
                                foreach (var field in statusType.GetFields(BindingFlags.Public | BindingFlags.Static))
                                {
                                    if (enabled && field.Name == "CREATED")
                                    {
                                        targetStatus = field.GetValue(null);
                                        break;
                                    }
                                    else if (!enabled && field.Name == "DISABLED")
                                    {
                                        targetStatus = field.GetValue(null);
                                        break;
                                    }
                                }

                                if (targetStatus != null)
                                {
                                    statusProp.SetValue(obj, targetStatus);
                                }
                                else
                                {
                                    throw new Exception($"Could not find appropriate status value for enabled={enabled}");
                                }
                            }
                            else
                            {
                                throw new Exception($"Status property is not an enum on type '{obj.GetType().Name}'");
                            }
                        }
                        else
                        {
                            throw new Exception($"Object of type '{obj.GetType().Name}' (ID: {id}) does not have a writable 'Status' property");
                        }
                    });

                    HttpContext.Response.StatusCode = 200;
                    return new ApiResponse
                    {
                        Success = true,
                        Error = null,

                    };
                }
                catch (Exception ex)
                {
                    HttpContext.Response.StatusCode = 400;
                    return new ApiResponse { Success = false, Error = ex.Message, StatusCode = 400, Type = "Error" };
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Error setting object enabled state: {ex}");
                HttpContext.Response.StatusCode = 500;
                return new ApiResponse { Success = false, Error = ex.Message, StatusCode = 400, Type = "Error" };
            }
        }

        /// <summary>
        /// <summary>
        /// POST /api/sequence/reset-status - Reset status for any object (item, trigger, or condition) by ID
        /// id: ID of the object
        /// </summary>
        [Route(HttpVerbs.Post, "/sequence/reset-status")]
        public ApiResponse ResetItemStatus([QueryField] string id)
        {
            try
            {
                if (string.IsNullOrEmpty(id))
                {
                    HttpContext.Response.StatusCode = 400;
                    return new ApiResponse { Success = false, Error = "id required", StatusCode = 400, Type = "Error" };
                }

                var obj = FindObjectById(id);
                if (obj == null)
                {
                    HttpContext.Response.StatusCode = 404;
                    return new ApiResponse { Success = false, Error = "Object not found", StatusCode = 404, Type = "Error" };
                }

                var runningRejection = RejectIfRunning(obj);
                if (runningRejection != null)
                    return runningRejection;

                try
                {
                    var mainContainer = GetMainContainer();
                    if (mainContainer == null)
                    {
                        HttpContext.Response.StatusCode = 400;
                        return new ApiResponse
                        {
                            Success = false,
                            Error = "No sequence loaded",
                        };
                    }

                    // Only reset if it's a sequence item
                    if (obj is ISequenceItem item)
                    {
                        Application.Current.Dispatcher.Invoke(() =>
                        {
                            // Reset this item and all subsequent items
                            ResetItemAndSubsequent(item, mainContainer);
                        });
                    }

                    HttpContext.Response.StatusCode = 200;
                    return new ApiResponse
                    {
                        Success = true,
                        Error = null,

                    };
                }
                catch (Exception ex)
                {
                    HttpContext.Response.StatusCode = 400;
                    return new ApiResponse { Success = false, Error = ex.Message, StatusCode = 400, Type = "Error" };
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Error resetting object status: {ex}");
                HttpContext.Response.StatusCode = 500;
                return new ApiResponse { Success = false, Error = ex.Message, StatusCode = 400, Type = "Error" };
            }
        }

        /// <summary>
        /// GET /api/sequence/metadata - Get metadata about any object (item, trigger, or condition) by ID
        /// id: ID of the object
        /// </summary>
        [Route(HttpVerbs.Get, "/sequence/metadata")]
        public ApiResponse GetItemMetadata([QueryField] string id)
        {
            try
            {
                if (string.IsNullOrEmpty(id))
                {
                    HttpContext.Response.StatusCode = 400;
                    return new ApiResponse { Success = false, Error = "id required", StatusCode = 400, Type = "Error" };
                }

                var obj = FindObjectById(id);
                if (obj == null)
                {
                    HttpContext.Response.StatusCode = 404;
                    return new ApiResponse { Success = false, Error = "Object not found", StatusCode = 400, Type = "Error" };
                }

                var metadata = new Dictionary<string, object>
                {
                    { "Name", ((dynamic)obj).Name },
                    { "Type", obj.GetType().Name },
                    { "FullType", obj.GetType().FullName },
                    { "Status", ((dynamic)obj).Status.ToString() },
                    { "Enabled", ((dynamic)obj).Status.ToString() != "DISABLED" }  // Enabled if status is not DISABLED
                };

                // Try to get description
                var descProp = obj.GetType().GetProperty("Description", BindingFlags.Public | BindingFlags.Instance);
                if (descProp?.CanRead == true)
                {
                    try { metadata["Description"] = descProp.GetValue(obj) ?? ""; }
                    catch { }
                }

                // Try to get category
                var catProp = obj.GetType().GetProperty("Category", BindingFlags.Public | BindingFlags.Instance);
                if (catProp?.CanRead == true)
                {
                    try { metadata["Category"] = catProp.GetValue(obj) ?? ""; }
                    catch { }
                }

                // Check if it's a container
                if (obj is ISequenceContainer container)
                {
                    metadata["IsContainer"] = true;
                    metadata["ItemCount"] = container.Items?.Count ?? 0;
                }

                // Check if it's a root container with triggers
                if (obj is ISequenceRootContainer root)
                {
                    metadata["HasTriggers"] = root.Triggers?.Count > 0;
                    metadata["TriggerCount"] = root.Triggers?.Count ?? 0;
                }

                // Create a hashtable from the metadata dictionary
                var metadata_hashtable = new Hashtable();
                foreach (var kvp in metadata)
                {
                    metadata_hashtable.Add(kvp.Key, kvp.Value);
                }

                HttpContext.Response.StatusCode = 200;
                WriteSequenceResponseData(HttpContext, metadata_hashtable);
                return null;
            }
            catch (Exception ex)
            {
                Logger.Error($"Error getting item metadata: {ex}");
                HttpContext.Response.StatusCode = 500;
                return new ApiResponse { Success = false, Error = ex.Message, StatusCode = 500, Type = "Error" };
            }
        }

        /// <summary>
        /// POST /api/sequence/clear - Clear all items from the sequence
        /// </summary>
        [Route(HttpVerbs.Post, "/sequence/clear")]
        public ApiResponse ClearSequence()
        {
            try
            {
                var sequenceMediator = TouchNStars.Mediators?.Sequence;
                if (sequenceMediator == null || !sequenceMediator.Initialized)
                {
                    HttpContext.Response.StatusCode = 503;
                    return new ApiResponse { Success = false, Error = "Sequence mediator not initialized", StatusCode = 400, Type = "Error" };
                }

                try
                {
                    var mainContainer = GetMainContainer();

                    if (mainContainer == null)
                    {
                        HttpContext.Response.StatusCode = 400;
                        return new ApiResponse { Success = false, Error = "No sequence loaded", StatusCode = 400, Type = "Error" };
                    }

                    if (sequenceMediator.IsAdvancedSequenceRunning())
                    {
                        HttpContext.Response.StatusCode = 409;
                        return new ApiResponse { Success = false, Error = "Cannot clear the sequence while it is running", StatusCode = 409, Type = "Error" };
                    }

                    var factory = GetFactory();
                    if (factory == null)
                    {
                        HttpContext.Response.StatusCode = 503;
                        return new ApiResponse { Success = false, Error = "Unable to access sequence factory", StatusCode = 503, Type = "Error" };
                    }

                    // The root container's DetachCommand asks for confirmation in a NINA message box,
                    // which blocks this request on Windows until someone clicks it at the PC.
                    // Load a fresh empty sequence instead.
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        var root = factory.GetContainer<SequenceRootContainer>();
                        root.Add(factory.GetContainer<StartAreaContainer>());
                        root.Add(factory.GetContainer<TargetAreaContainer>());
                        root.Add(factory.GetContainer<EndAreaContainer>());
                        sequenceMediator.SetAdvancedSequence(root);
                    });

                    ResetIdCounterAndMap();

                    HttpContext.Response.StatusCode = 200;
                    return new ApiResponse
                    {
                        Success = true,
                        Error = null,

                    };
                }
                catch (Exception ex)
                {
                    Logger.Error($"Error clearing sequence: {ex}");
                    HttpContext.Response.StatusCode = 500;
                    return new ApiResponse { Success = false, Error = ex.Message, StatusCode = 400, Type = "Error" };
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Error in clear endpoint: {ex}");
                HttpContext.Response.StatusCode = 500;
                return new ApiResponse { Success = false, Error = ex.Message, StatusCode = 400, Type = "Error" };
            }
        }

        /// <summary>
        /// POST /api/sequence/reset - Reset the sequence progress (clears all status but keeps items and configuration)
        /// </summary>
        [Route(HttpVerbs.Post, "/sequence/reset")]
        public ApiResponse ResetSequence()
        {
            try
            {
                var sequenceMediator = TouchNStars.Mediators?.Sequence;
                if (sequenceMediator == null || !sequenceMediator.Initialized)
                {
                    HttpContext.Response.StatusCode = 503;
                    return new ApiResponse { Success = false, Error = "Sequence mediator not initialized", StatusCode = 400, Type = "Error" };
                }

                try
                {
                    var mainContainer = GetMainContainer();

                    if (mainContainer == null)
                    {
                        HttpContext.Response.StatusCode = 400;
                        return new ApiResponse { Success = false, Error = "No sequence loaded", StatusCode = 400, Type = "Error" };
                    }

                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        mainContainer.ResetProgressCommand?.Execute(null);
                    });

                    HttpContext.Response.StatusCode = 200;
                    return new ApiResponse
                    {
                        Success = true,
                        Error = null,

                    };
                }
                catch (Exception ex)
                {
                    Logger.Error($"Error resetting sequence: {ex}");
                    HttpContext.Response.StatusCode = 500;
                    return new ApiResponse { Success = false, Error = ex.Message, StatusCode = 400, Type = "Error" };
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Error in reset endpoint: {ex}");
                HttpContext.Response.StatusCode = 500;
                return new ApiResponse { Success = false, Error = ex.Message, StatusCode = 400, Type = "Error" };
            }
        }

        /// <summary>
        /// POST /api/sequence/start - Start the sequence execution
        /// skipValidation: Whether to skip sequence validation before starting (default: false)
        /// </summary>
        [Route(HttpVerbs.Post, "/sequence/start")]
        public async Task<ApiResponse> StartSequence([QueryField] bool skipValidation = false)
        {
            try
            {
                var sequenceMediator = TouchNStars.Mediators?.Sequence;
                if (sequenceMediator == null || !sequenceMediator.Initialized)
                {
                    HttpContext.Response.StatusCode = 503;
                    return new ApiResponse { Success = false, Error = "Sequence mediator not initialized", StatusCode = 400, Type = "Error" };
                }

                try
                {
                    await sequenceMediator.StartAdvancedSequence(skipValidation);

                    HttpContext.Response.StatusCode = 200;
                    return new ApiResponse
                    {
                        Success = true,
                        Error = null,
                        StatusCode = 200,
                        Type = "Success"
                    };
                }
                catch (Exception ex)
                {
                    Logger.Error($"Error starting sequence: {ex}");
                    HttpContext.Response.StatusCode = 500;
                    return new ApiResponse { Success = false, Error = ex.Message, StatusCode = 400, Type = "Error" };
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Error in start endpoint: {ex}");
                HttpContext.Response.StatusCode = 500;
                return new ApiResponse { Success = false, Error = ex.Message, StatusCode = 400, Type = "Error" };
            }
        }

        /// <summary>
        /// GET /api/sequence/current-running-item - Get the ID of the currently running sequence item
        /// Uses the built-in GetCurrentRunningItems() method on the root container (same as WPF)
        /// </summary>
        [Route(HttpVerbs.Get, "/sequence/current-running-item")]
        public ApiResponse GetCurrentItem()
        {
            try
            {
                var mainContainer = GetMainContainer();
                if (mainContainer == null)
                {
                    HttpContext.Response.StatusCode = 503;
                    return new ApiResponse { Success = false, Error = "No sequence loaded", StatusCode = 503, Type = "Error" };
                }

                try
                {
                    // GetCurrentRunningItems() only exists in the PINS NINA fork, so walk the
                    // tree ourselves to stay compatible with official NINA builds.
                    var currentItem = FindDeepestRunningItem(mainContainer);
                    if (currentItem == null)
                    {
                        HttpContext.Response.StatusCode = 200;
                        return new ApiResponse { Success = true, Error = null, StatusCode = 200, Type = "NoCurrentItem" };
                    }

                    string itemId = GetOrCreateId(currentItem, ObjectType.Item);

                    var response = new Hashtable
                    {
                        { "Id", itemId },
                        { "Name", ((dynamic)currentItem).Name },
                        { "Type", currentItem.GetType().Name }
                    };

                    HttpContext.Response.StatusCode = 200;
                    WriteSequenceResponseData(HttpContext, response);
                    return null;
                }
                catch (Exception ex)
                {
                    Logger.Error($"Error getting current item: {ex}");
                    HttpContext.Response.StatusCode = 500;
                    return new ApiResponse { Success = false, Error = ex.Message, StatusCode = 500, Type = "Error" };
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Error in current-item endpoint: {ex}");
                HttpContext.Response.StatusCode = 500;
                return new ApiResponse { Success = false, Error = ex.Message, StatusCode = 500, Type = "Error" };
            }
        }

        /// <summary>
        /// Returns the deepest RUNNING item below the given container, or null if nothing runs.
        /// </summary>
        private static ISequenceItem FindDeepestRunningItem(ISequenceContainer container)
        {
            foreach (var item in container.Items.ToList())
            {
                if (item.Status != NINA.Core.Enum.SequenceEntityStatus.RUNNING)
                    continue;

                if (item is ISequenceContainer child)
                {
                    var deeper = FindDeepestRunningItem(child);
                    return deeper ?? item;
                }
                return item;
            }
            return null;
        }

        /// <summary>
        /// POST /api/sequence/stop - Stop the sequence execution
        /// </summary>
        [Route(HttpVerbs.Post, "/sequence/stop")]
        public ApiResponse StopSequence()
        {
            try
            {
                var sequenceMediator = TouchNStars.Mediators?.Sequence;
                if (sequenceMediator == null || !sequenceMediator.Initialized)
                {
                    HttpContext.Response.StatusCode = 503;
                    return new ApiResponse { Success = false, Error = "Sequence mediator not initialized", StatusCode = 400, Type = "Error" };
                }

                try
                {
                    sequenceMediator.CancelAdvancedSequence();

                    HttpContext.Response.StatusCode = 200;
                    return new ApiResponse
                    {
                        Success = true,
                        Error = null,

                    };
                }
                catch (Exception ex)
                {
                    Logger.Error($"Error stopping sequence: {ex}");
                    HttpContext.Response.StatusCode = 500;
                    return new ApiResponse { Success = false, Error = ex.Message, StatusCode = 400, Type = "Error" };
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Error in stop endpoint: {ex}");
                HttpContext.Response.StatusCode = 500;
                return new ApiResponse { Success = false, Error = ex.Message, StatusCode = 400, Type = "Error" };
            }
        }

        /// <summary>
        /// POST /api/sequence/skip-to-end - Skip to the end of the sequence
        /// </summary>
        [Route(HttpVerbs.Post, "/sequence/skip-to-end")]
        public ApiResponse SkipToEnd()
        {
            try
            {
                var sequenceMediator = TouchNStars.Mediators?.Sequence;
                if (sequenceMediator == null || !sequenceMediator.Initialized)
                {
                    HttpContext.Response.StatusCode = 503;
                    return new ApiResponse { Success = false, Error = "Sequence mediator not initialized", StatusCode = 400, Type = "Error" };
                }

                try
                {
                    var sequence2VM = GetSequence2VM();
                    if (sequence2VM == null)
                    {
                        HttpContext.Response.StatusCode = 400;
                        return new ApiResponse { Success = false, Error = "Sequence2VM not available", StatusCode = 400, Type = "Error" };
                    }

                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        // Use reflection to access SkipToEndOfSequenceCommand (not in interface)
                        var cmdProp = sequence2VM.GetType().GetProperty("SkipToEndOfSequenceCommand", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                        if (cmdProp != null)
                        {
                            var cmd = cmdProp.GetValue(sequence2VM) as ICommand;
                            cmd?.Execute(null);
                        }
                    });

                    HttpContext.Response.StatusCode = 200;
                    return new ApiResponse
                    {
                        Success = true,
                        Error = null,

                    };
                }
                catch (Exception ex)
                {
                    Logger.Error($"Error skipping to end: {ex}");
                    HttpContext.Response.StatusCode = 500;
                    return new ApiResponse { Success = false, Error = ex.Message, StatusCode = 400, Type = "Error" };
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Error in skip-to-end endpoint: {ex}");
                HttpContext.Response.StatusCode = 500;
                return new ApiResponse { Success = false, Error = ex.Message, StatusCode = 400, Type = "Error" };
            }
        }

        /// <summary>
        /// POST /api/sequence/skip-current-item - Skip the current item and move to the next one
        /// </summary>
        [Route(HttpVerbs.Post, "/sequence/skip-current-item")]
        public ApiResponse SkipCurrentItem()
        {
            try
            {
                var sequenceMediator = TouchNStars.Mediators?.Sequence;
                if (sequenceMediator == null || !sequenceMediator.Initialized)
                {
                    HttpContext.Response.StatusCode = 503;
                    return new ApiResponse { Success = false, Error = "Sequence mediator not initialized", StatusCode = 400, Type = "Error" };
                }

                try
                {
                    var sequence2VM = GetSequence2VM();
                    if (sequence2VM == null)
                    {
                        HttpContext.Response.StatusCode = 400;
                        return new ApiResponse { Success = false, Error = "Sequence2VM not available", StatusCode = 400, Type = "Error" };
                    }

                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        // Use reflection to access SkipCurrentItemCommand (not in interface)
                        var cmdProp = sequence2VM.GetType().GetProperty("SkipCurrentItemCommand", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                        if (cmdProp != null)
                        {
                            var cmd = cmdProp.GetValue(sequence2VM) as ICommand;
                            cmd?.Execute(null);
                        }
                    });

                    HttpContext.Response.StatusCode = 200;
                    return new ApiResponse
                    {
                        Success = true,
                        Error = null,

                    };
                }
                catch (Exception ex)
                {
                    Logger.Error($"Error skipping current item: {ex}");
                    HttpContext.Response.StatusCode = 500;
                    return new ApiResponse { Success = false, Error = ex.Message, StatusCode = 400, Type = "Error" };
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Error in skip-current-item endpoint: {ex}");
                HttpContext.Response.StatusCode = 500;
                return new ApiResponse { Success = false, Error = ex.Message, StatusCode = 400, Type = "Error" };
            }
        }

        /// <summary>
        /// Helper method to get the main sequence container from the sequence mediator.
        /// Uses reflection to navigate through the internal structure.
        /// </summary>
        internal static ISequenceRootContainer GetMainContainer()
        {
            try
            {
                var sequenceMediator = TouchNStars.Mediators?.Sequence;
                if (sequenceMediator == null || !sequenceMediator.Initialized)
                    return null;

                var mediator = (SequenceMediator)sequenceMediator;
                object nav = mediator.GetType()
                    .GetField("sequenceNavigation", BindingFlags.NonPublic | BindingFlags.Instance)
                    .GetValue(mediator);

                dynamic navVM = nav;
                var sequence2VM = (NINA.ViewModel.Sequencer.ISequence2VM)navVM.Sequence2VM;
                ISequencer sequencer = sequence2VM.Sequencer;
                return sequencer.MainContainer;
            }
            catch (Exception ex)
            {
                Logger.Error($"Error getting main container: {ex}");
                return null;
            }
        }

        /// <summary>
        /// Helper method to get the Sequence2VM from the sequence mediator
        /// </summary>
        private NINA.ViewModel.Sequencer.ISequence2VM GetSequence2VM()
        {
            try
            {
                var sequenceMediator = TouchNStars.Mediators?.Sequence;
                if (sequenceMediator == null || !sequenceMediator.Initialized)
                    return null;

                var mediator = (SequenceMediator)sequenceMediator;
                object nav = mediator.GetType()
                    .GetField("sequenceNavigation", BindingFlags.NonPublic | BindingFlags.Instance)
                    .GetValue(mediator);

                dynamic navVM = nav;
                return (NINA.ViewModel.Sequencer.ISequence2VM)navVM.Sequence2VM;
            }
            catch (Exception ex)
            {
                Logger.Error($"Error getting Sequence2VM: {ex}");
                return null;
            }
        }

        /// <summary>
        /// Helper method to get the sequence factory from the mediator
        /// </summary>
        private ISequencerFactory GetFactory()
        {
            try
            {
                var sequenceMediator = TouchNStars.Mediators?.Sequence;
                if (sequenceMediator == null || !sequenceMediator.Initialized)
                    return null;

                var mediator = (SequenceMediator)sequenceMediator;
                object nav = mediator.GetType()
                    .GetField("sequenceNavigation", BindingFlags.NonPublic | BindingFlags.Instance)
                    .GetValue(mediator);

                dynamic navVM = nav;
                var factoryField = navVM?.GetType().GetField("factory", BindingFlags.NonPublic | BindingFlags.Instance);
                return factoryField?.GetValue(navVM) as ISequencerFactory;
            }
            catch (Exception ex)
            {
                Logger.Error($"Error getting factory: {ex}");
                return null;
            }
        }

        /// <summary>
        /// Generate a unique ID for tracking items/triggers/conditions
        /// </summary>
        private static string GenerateId()
        {
            // Only called while idLock is held
            return $"id_{++idCounter}";
        }

        /// <summary>
        /// Get or generate an ID for an object, reusing existing IDs from the map
        /// </summary>
        private static string GetOrCreateId(object obj, ObjectType type)
        {
            lock (idLock)
            {
                if (idByObject.TryGetValue(obj, out var existingId))
                    return existingId;

                string newId = GenerateId();
                objectIdMap[newId] = new TrackedObject { Value = obj, Type = type, Seq = idCounter };
                idByObject[obj] = newId;
                return newId;
            }
        }

        /// <summary>
        /// Remove an ID (and its reverse entry) from the registry
        /// </summary>
        private static void UntrackId(string id)
        {
            if (string.IsNullOrEmpty(id))
                return;
            lock (idLock)
            {
                if (objectIdMap.Remove(id, out var tracked))
                    idByObject.Remove(tracked.Value);
            }
        }

        /// <summary>
        /// Remove an object from the registry, if it is tracked
        /// </summary>
        private static void UntrackObject(object obj)
        {
            lock (idLock)
            {
                if (idByObject.Remove(obj, out var id))
                    objectIdMap.Remove(id);
            }
        }

        private static bool TryGetTracked(string id, out TrackedObject tracked)
        {
            tracked = null;
            if (string.IsNullOrEmpty(id))
                return false;
            lock (idLock)
            {
                return objectIdMap.TryGetValue(id, out tracked);
            }
        }

        /// <summary>
        /// Reset the ID map - call when a new sequence is loaded or cleared.
        /// The counter keeps running on purpose: a client still holding an old ID must get a
        /// "not found" instead of silently hitting an object of the new sequence.
        /// </summary>
        private static void ResetIdCounterAndMap()
        {
            lock (idLock)
            {
                objectIdMap.Clear();
                idByObject.Clear();
                lastLoadedSequence = null;
            }
        }

        /// <summary>
        /// Drops all IDs when NINA switched to a different root container (e.g. a sequence loaded
        /// from the NINA UI instead of through this API), so stale objects are not kept alive.
        /// </summary>
        private static void EnsureIdScope(ISequenceRootContainer root)
        {
            lock (idLock)
            {
                if (ReferenceEquals(lastLoadedSequence, root))
                    return;
                objectIdMap.Clear();
                idByObject.Clear();
                lastLoadedSequence = root;
            }
        }

        private static long CurrentIdSeq()
        {
            lock (idLock)
            {
                return idCounter;
            }
        }

        /// <summary>
        /// Drops every ID whose object was not seen in a complete walk of the tree. Items removed
        /// by NINA or by plugins like Target Scheduler would otherwise stay referenced (and in
        /// memory) all night long. IDs handed out after the walk started are kept, since their
        /// objects may have been added while it ran.
        /// </summary>
        private static void PruneRegistry(HashSet<object> seen, long walkStartSeq)
        {
            lock (idLock)
            {
                var stale = objectIdMap
                    .Where(kv => kv.Value.Seq <= walkStartSeq && !seen.Contains(kv.Value.Value))
                    .ToList();
                foreach (var kv in stale)
                {
                    objectIdMap.Remove(kv.Key);
                    idByObject.Remove(kv.Value.Value);
                }
            }
        }

        /// <summary>
        /// Get the display name for an item, using factory template if item's name is null or just the type name
        /// </summary>
        private string GetDisplayName(object obj)
        {
            if (obj == null)
                return "Unknown";

            var objType = obj.GetType();

            // Get the Name property using reflection (works for items, triggers, and conditions)
            var nameProperty = objType.GetProperty("Name");
            var nameValue = nameProperty?.GetValue(obj) as string;

            if (!string.IsNullOrEmpty(nameValue) && nameValue != objType.Name)
                return nameValue;

            // Try to find a factory template with a proper name
            var factory = GetFactory();
            if (factory != null)
            {
                object templateObject = null;

                if (obj is ISequenceItem)
                    templateObject = factory.Items?.FirstOrDefault(i => i.GetType() == objType);
                else if (obj is ISequenceTrigger)
                    templateObject = factory.Triggers?.FirstOrDefault(t => t.GetType() == objType);
                else if (obj is ISequenceCondition)
                    templateObject = factory.Conditions?.FirstOrDefault(c => c.GetType() == objType);

                if (templateObject != null)
                {
                    var templateName = templateObject.GetType().GetProperty("Name")?.GetValue(templateObject) as string;
                    if (!string.IsNullOrEmpty(templateName) && templateName != objType.Name)
                        return templateName;
                }
            }

            // Fall back to type name
            return objType.Name;
        }

        /// <summary>
        /// Track an item by ID for later lookup - reuses existing ID if already tracked
        /// </summary>
        private string TrackItem(ISequenceItem item)
        {
            return GetOrCreateId(item, ObjectType.Item);
        }

        /// <summary>
        /// Recursively track all items in a container hierarchy, returns the ID of the primary item
        /// </summary>
        private string TrackItemRecursive(ISequenceItem item)
        {
            var primaryItemId = TrackItem(item);

            if (item is ISequenceContainer container)
            {
                // Track all child items
                foreach (var child in container.Items)
                {
                    TrackItemRecursive(child);
                }

                // Track all triggers
                var seqContainer = item as SequenceContainer;
                if (seqContainer != null)
                {
                    foreach (var trigger in seqContainer.Triggers)
                    {
                        TrackTrigger(trigger);
                    }

                    foreach (var condition in seqContainer.Conditions)
                    {
                        TrackCondition(condition);
                    }
                }
            }

            return primaryItemId;
        }

        /// <summary>
        /// Remove tracking for an item and all its descendants (child items, triggers, conditions)
        /// </summary>
        private void UntrackItemRecursive(ISequenceItem item)
        {
            // Untrack the item itself
            UntrackObject(item);

            if (item is ISequenceContainer container)
            {
                // Untrack all child items
                foreach (var child in container.Items)
                {
                    UntrackItemRecursive(child);
                }

                // Untrack all triggers and conditions
                var seqContainer = item as SequenceContainer;
                if (seqContainer != null)
                {
                    foreach (var trigger in seqContainer.Triggers)
                    {
                        UntrackObject(trigger);
                    }

                    foreach (var condition in seqContainer.Conditions)
                    {
                        UntrackObject(condition);
                    }
                }
            }
        }

        /// <summary>
        /// Track a trigger by ID for later lookup - reuses existing ID if already tracked
        /// </summary>
        private string TrackTrigger(ISequenceTrigger trigger)
        {
            return GetOrCreateId(trigger, ObjectType.Trigger);
        }

        /// <summary>
        /// Track a condition by ID for later lookup - reuses existing ID if already tracked
        /// </summary>
        private string TrackCondition(ISequenceCondition condition)
        {
            return GetOrCreateId(condition, ObjectType.Condition);
        }

        /// <summary>
        /// Find an item by its tracked ID
        /// </summary>
        private ISequenceItem FindItemById(string id)
        {
            return TryGetTracked(id, out var tracked) && tracked.Type == ObjectType.Item
                ? tracked.Value as ISequenceItem
                : null;
        }

        /// <summary>
        /// Get the type of an object by its ID
        /// </summary>
        private ObjectType? GetObjectType(string id)
        {
            return TryGetTracked(id, out var tracked) ? tracked.Type : null;
        }

        /// <summary>
        /// Find any object (item, trigger, or condition) by its ID
        /// </summary>
        private object FindObjectById(string id)
        {
            return TryGetTracked(id, out var tracked) ? tracked.Value : null;
        }

        /// <summary>
        /// Find the container that holds the given trigger
        /// </summary>
        private ISequenceContainer FindTriggerContainer(ISequenceContainer container, ISequenceTrigger trigger)
        {
            // Check if this container has the trigger
            var triggersProperty = container.GetType().GetProperty("Triggers", BindingFlags.Public | BindingFlags.Instance);
            if (triggersProperty != null && triggersProperty.CanRead)
            {
                var triggers = triggersProperty.GetValue(container) as System.Collections.IList;
                if (triggers != null && triggers.Contains(trigger))
                    return container;
            }

            // Recursively search child items
            foreach (var item in container.Items)
            {
                if (item is ISequenceContainer childContainer)
                {
                    var result = FindTriggerContainer(childContainer, trigger);
                    if (result != null)
                        return result;
                }
            }

            return null;
        }

        /// <summary>
        /// Find the container that holds the given condition
        /// </summary>
        private ISequenceContainer FindConditionContainer(ISequenceContainer container, ISequenceCondition condition)
        {
            // Check if this container has the condition
            var conditionsProperty = container.GetType().GetProperty("Conditions", BindingFlags.Public | BindingFlags.Instance);
            if (conditionsProperty != null && conditionsProperty.CanRead)
            {
                var conditions = conditionsProperty.GetValue(container) as System.Collections.IList;
                if (conditions != null && conditions.Contains(condition))
                    return container;
            }

            // Recursively search child items
            foreach (var item in container.Items)
            {
                if (item is ISequenceContainer childContainer)
                {
                    var result = FindConditionContainer(childContainer, condition);
                    if (result != null)
                        return result;
                }
            }

            return null;
        }

        /// <summary>
        /// Find the item that contains the given condition
        /// </summary>
        private ISequenceItem FindItemWithCondition(ISequenceContainer container, ISequenceCondition condition)
        {
            // Check items in this container
            foreach (var item in container.Items)
            {
                var conditionsProp = item.GetType().GetProperty("Conditions", BindingFlags.Public | BindingFlags.Instance);
                if (conditionsProp != null && conditionsProp.CanRead)
                {
                    var conditions = conditionsProp.GetValue(item) as System.Collections.IList;
                    if (conditions != null && conditions.Contains(condition))
                        return item;
                }

                // Recursively search if item is a container
                if (item is ISequenceContainer childContainer)
                {
                    var result = FindItemWithCondition(childContainer, condition);
                    if (result != null)
                        return result;
                }
            }

            return null;
        }

        /// <summary>
        /// Find the item that contains the given condition (searches from root)
        /// </summary>
        private ISequenceItem FindItemWithCondition(ISequenceCondition condition)
        {
            try
            {
                var mainContainer = GetMainContainer();
                if (mainContainer == null)
                    return null;

                return FindItemWithCondition(mainContainer, condition);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Calculate the index path for an item in the sequence
        /// </summary>
        private string CalculateIndexPathForItem(ISequenceItem item)
        {
            try
            {
                var mainContainer = GetMainContainer();
                if (mainContainer == null)
                    return null;

                var path = new List<int>();
                return FindItemInContainer(mainContainer, item, path) ? string.Join(",", path) : null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Recursively find an item in the container hierarchy and build its path
        /// </summary>
        private bool FindItemInContainer(ISequenceContainer container, ISequenceItem targetItem, List<int> path)
        {
            for (int i = 0; i < container.Items.Count; i++)
            {
                if (container.Items[i] == targetItem)
                {
                    path.Add(i);
                    return true;
                }

                if (container.Items[i] is ISequenceContainer nestedContainer)
                {
                    var nestedPath = new List<int>(path) { i };
                    if (FindItemInContainer(nestedContainer, targetItem, nestedPath))
                    {
                        path.Clear();
                        path.AddRange(nestedPath);
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// Helper method to recursively reset an item and all subsequent items in the sequence
        /// </summary>
        private void ResetItemAndSubsequent(ISequenceItem item, ISequenceRootContainer rootContainer)
        {
            // Runs on the UI thread. Every item is reset exactly once: a recursive call per
            // following sibling would reset each of them again for every predecessor, which is
            // 2^n resets and freezes NINA for a container with a few dozen instructions.
            ResetSingleItem(item);

            // Cascade the reset up to parent containers (matches WPF behavior)
            item.ResetProgressCascaded();

            ISequenceContainer parentContainer = null;
            FindItemContainer(rootContainer, item, ref parentContainer);
            if (parentContainer == null)
                return;

            var siblings = parentContainer.Items.ToArray();
            var itemIndex = Array.IndexOf(siblings, item);
            for (int i = itemIndex + 1; itemIndex >= 0 && i < siblings.Length; i++)
            {
                // A following sibling can be the one the sequencer executes right now (the reset
                // item finished before it) - leave it and its loop counters alone.
                if (IsRunning(siblings[i]))
                    continue;
                ResetSingleItem(siblings[i]);
            }
        }

        private static void ResetSingleItem(ISequenceItem item)
        {
            // ResetAll() also resets triggers, conditions and loop counters of containers
            if (item is ISequenceContainer container)
                container.ResetAll();
            else
                item.ResetProgress();
        }

        /// <summary>
        /// Helper to find the container that directly holds an item
        /// </summary>
        private void FindItemContainer(ISequenceContainer container, ISequenceItem targetItem, ref ISequenceContainer parentContainer)
        {
            if (parentContainer != null) return; // Already found

            foreach (var item in container.Items)
            {
                if (item == targetItem)
                {
                    parentContainer = container;
                    return;
                }

                if (item is ISequenceContainer childContainer)
                {
                    FindItemContainer(childContainer, targetItem, ref parentContainer);
                }
            }
        }

        /// <summary>
        /// Recursively search for and remove an item from a container and its children
        /// </summary>
        private bool RemoveItemRecursive(ISequenceContainer container, ISequenceItem item)
        {
            if (container == null)
                return false;

            // Try to remove from current container's items
            if (container.Items.Contains(item))
            {
                // First untrack the item and ALL its descendants recursively
                UntrackItemRecursive(item);

                // Then use NINA's Remove method which handles parent detachment and cascading cleanup
                var seqContainer = container as SequenceContainer;
                if (seqContainer != null)
                {
                    return seqContainer.Remove(item);
                }
                else
                {
                    // Fallback for containers that don't implement SequenceContainer
                    container.Items.Remove(item);
                    return true;
                }
            }

            // Recursively search in child containers
            foreach (var childItem in container.Items)
            {
                if (childItem is ISequenceContainer childContainer)
                {
                    if (RemoveItemRecursive(childContainer, item))
                        return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Remove tracking for a trigger
        /// </summary>
        private void UntrackTrigger(ISequenceTrigger trigger)
        {
            UntrackObject(trigger);
        }

        /// <summary>
        /// Remove tracking for a condition
        /// </summary>
        private void UntrackCondition(ISequenceCondition condition)
        {
            UntrackObject(condition);
        }

        /// <summary>
        /// Recursively search for and remove a trigger from a container and its children
        /// </summary>
        private bool RemoveTriggerRecursive(ISequenceContainer container, ISequenceTrigger trigger)
        {
            if (container == null)
                return false;

            // Try to remove from current container's triggers
            if (container is SequenceContainer seqContainer && seqContainer.Triggers.Contains(trigger))
            {
                UntrackTrigger(trigger);
                seqContainer.Triggers.Remove(trigger);
                return true;
            }

            // Recursively search in child items
            foreach (var item in container.Items)
            {
                if (item is ISequenceContainer childContainer)
                {
                    if (RemoveTriggerRecursive(childContainer, trigger))
                        return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Recursively search for and remove a condition from a container and its children
        /// </summary>
        private bool RemoveConditionRecursive(ISequenceContainer container, ISequenceCondition condition)
        {
            if (container == null)
                return false;

            // Try to remove from current container's conditions
            if (container is SequenceContainer seqContainer && seqContainer.Conditions.Contains(condition))
            {
                UntrackCondition(condition);
                seqContainer.Conditions.Remove(condition);
                return true;
            }

            // Recursively search in child items
            foreach (var item in container.Items)
            {
                if (item is ISequenceContainer childContainer)
                {
                    if (RemoveConditionRecursive(childContainer, condition))
                        return true;
                }
            }

            return false;
        }

        private static readonly string[] ignoredProperties = {
            "Name", "Status", "IsExpanded", "ErrorBehavior", "Attempts", "CoordsFromPlanetariumCommand", "ExposureInfoListExpanded", "CoordsToFramingCommand",
            "DeleteExposureInfoCommand", "ExposureInfoList", "DateTimeProviders", "ImageTypes", "DropTargetCommand", "DateTime", "ProfileService", "Parent",
            "InfoButtonColor", "Icon" };

        private static List<Hashtable> getTriggers(SequenceContainer sequence)
        {
            List<Hashtable> triggers = new List<Hashtable>();
            foreach (var trigger in sequence.Triggers)
            {
                try
                {
                    Hashtable triggerTable = new Hashtable
                    {
                        { "Id", GetOrCreateId(trigger, ObjectType.Trigger) },
                        { "Name", trigger.Name },
                        { "Status", trigger.Status.ToString() },
                        { "FullTypeName", trigger.GetType().FullName }
                    };

                    var proper = trigger.GetType().GetProperties().Where(p => p.MemberType == MemberTypes.Property && !ignoredProperties.Contains(p.Name) && !typeof(SequenceTrigger).GetProperties().Any(x => x.Name == p.Name));
                    foreach (var prop in proper)
                    {
                        if (prop.CanWrite && (prop.GetSetMethod(true)?.IsPublic ?? false) && prop.CanRead && (prop.GetGetMethod(true)?.IsPublic ?? false))
                        {
                            try { triggerTable[prop.Name] = SafeSerializeValue(prop.GetValue(trigger)); }
                            catch (Exception ex) { Logger.Debug($"Failed to read trigger property {prop.Name}: {ex.Message}"); }
                        }
                    }

                    // Expand WaitLoopData progress fields for triggers with Data property
                    try
                    {
                        var dataProperty = trigger.GetType().GetProperty("Data", BindingFlags.Public | BindingFlags.Instance);
                        if (dataProperty != null && dataProperty.CanRead)
                        {
                            var dataValue = dataProperty.GetValue(trigger);
                            if (dataValue != null)
                            {
                                var dataType = dataValue.GetType();
                                var currentAltProp = dataType.GetProperty("CurrentAltitude", BindingFlags.Public | BindingFlags.Instance);
                                var targetAltProp = dataType.GetProperty("TargetAltitude", BindingFlags.Public | BindingFlags.Instance);
                                var expectedTimeProp = dataType.GetProperty("ExpectedTime", BindingFlags.Public | BindingFlags.Instance);
                                var comparatorProp = dataType.GetProperty("Comparator", BindingFlags.Public | BindingFlags.Instance);

                                if (currentAltProp != null && targetAltProp != null && expectedTimeProp != null && comparatorProp != null)
                                {
                                    triggerTable["CurrentAltitude"] = currentAltProp.GetValue(dataValue);
                                    triggerTable["TargetAltitude"] = targetAltProp.GetValue(dataValue);
                                    triggerTable["ExpectedTime"] = expectedTimeProp.GetValue(dataValue);
                                    triggerTable["Comparator"] = comparatorProp.GetValue(dataValue)?.ToString();
                                }
                            }
                        }
                    }
                    catch { /* Silently ignore if Data property doesn't exist or can't be accessed */ }

                    // RemainingTime is read-only for TimeSpanCondition/TimeCondition triggers
                    if (trigger is TimeSpanCondition timeSpanTrigger)
                    {
                        triggerTable["RemainingTime"] = timeSpanTrigger.RemainingTime.ToString(@"hh\:mm\:ss");
                    }
                    else if (trigger is TimeCondition timeTrigger)
                    {
                        triggerTable["RemainingTime"] = timeTrigger.RemainingTime.ToString(@"hh\:mm\:ss");
                    }

                    // SafetyMonitorCondition - IsSafe has a protected setter
                    if (trigger is SafetyMonitorCondition safetyTrigger)
                    {
                        triggerTable["IsSafe"] = safetyTrigger.IsSafe;
                    }

                    triggers.Add(triggerTable);
                }
                catch (Exception ex)
                {
                    Logger.Error(ex);
                }
            }

            return triggers;
        }

        private static List<Hashtable> getConditions(SequenceContainer sequence)
        {
            List<Hashtable> conditions = new List<Hashtable>();
            foreach (var condition in sequence.Conditions)
            {
                try
                {
                    Hashtable ctable = new Hashtable
                    {
                        { "Id", GetOrCreateId(condition, ObjectType.Condition) },
                        { "Name", condition.Name },
                        { "Status", condition.Status.ToString() },
                        { "FullTypeName", condition.GetType().FullName }
                    };

                    var proper = condition.GetType().GetProperties().Where(p => p.MemberType == MemberTypes.Property && !ignoredProperties.Contains(p.Name) && !typeof(SequenceCondition).GetProperties().Any(x => x.Name == p.Name));
                    foreach (var prop in proper)
                    {
                        if (prop.CanWrite && (prop.GetSetMethod(true)?.IsPublic ?? false) && prop.CanRead && (prop.GetGetMethod(true)?.IsPublic ?? false))
                        {
                            try { ctable[prop.Name] = SafeSerializeValue(prop.GetValue(condition)); }
                            catch (Exception ex) { Logger.Debug($"Failed to read condition property {prop.Name}: {ex.Message}"); }
                        }
                    }

                    // Expand WaitLoopData progress fields for conditions with Data property
                    try
                    {
                        var dataProperty = condition.GetType().GetProperty("Data", BindingFlags.Public | BindingFlags.Instance);
                        if (dataProperty != null && dataProperty.CanRead)
                        {
                            var dataValue = dataProperty.GetValue(condition);
                            if (dataValue != null)
                            {
                                var dataType = dataValue.GetType();
                                var currentAltProp = dataType.GetProperty("CurrentAltitude", BindingFlags.Public | BindingFlags.Instance);
                                var targetAltProp = dataType.GetProperty("TargetAltitude", BindingFlags.Public | BindingFlags.Instance);
                                var expectedTimeProp = dataType.GetProperty("ExpectedTime", BindingFlags.Public | BindingFlags.Instance);
                                var comparatorProp = dataType.GetProperty("Comparator", BindingFlags.Public | BindingFlags.Instance);

                                if (currentAltProp != null && targetAltProp != null && expectedTimeProp != null && comparatorProp != null)
                                {
                                    ctable["CurrentAltitude"] = currentAltProp.GetValue(dataValue);
                                    ctable["TargetAltitude"] = targetAltProp.GetValue(dataValue);
                                    ctable["ExpectedTime"] = expectedTimeProp.GetValue(dataValue);
                                    ctable["Comparator"] = comparatorProp.GetValue(dataValue)?.ToString();
                                }
                            }
                        }
                    }
                    catch { /* Silently ignore if Data property doesn't exist or can't be accessed */ }

                    // TimeSpanCondition/TimeCondition - RemainingTime is read-only (no public setter)
                    if (condition is TimeSpanCondition timeSpanCond)
                    {
                        ctable["RemainingTime"] = timeSpanCond.RemainingTime.ToString(@"hh\:mm\:ss");
                    }
                    else if (condition is TimeCondition timeCond)
                    {
                        ctable["RemainingTime"] = timeCond.RemainingTime.ToString(@"hh\:mm\:ss");
                    }

                    // SafetyMonitorCondition - IsSafe has a protected setter
                    if (condition is SafetyMonitorCondition safetyCond)
                    {
                        ctable["IsSafe"] = safetyCond.IsSafe;
                    }

                    conditions.Add(ctable);
                }
                catch (Exception ex)
                {
                    Logger.Error(ex);
                }

            }
            return conditions;
        }

        private static List<Hashtable> getSequenceRecursively(ISequenceContainer sequence)
        {
            List<Hashtable> result = new List<Hashtable>();

            foreach (var item in sequence.Items)
            {
                try
                {
                    Hashtable it = new Hashtable
                    {
                        { "Id", GetOrCreateId(item, ObjectType.Item) },
                        { "Name", item.Name },
                        { "Status", item.Status.ToString() },
                        { "FullTypeName", item.GetType().FullName }
                    };

                    if (item is ISequenceContainer container)
                    {
                        it["Name"] = item.Name;
                        it.Add("Items", getSequenceRecursively(container));
                        if (container is SequenceContainer sc)
                        {
                            it.Add("Conditions", getConditions(sc));
                            it.Add("Triggers", getTriggers(sc));
                        }
                    }

                    var proper = item.GetType().GetProperties().Where(p => p.MemberType == MemberTypes.Property && !ignoredProperties.Contains(p.Name) && !typeof(SequenceItem).GetProperties().Any(x => x.Name == p.Name));
                    foreach (var prop in proper)
                    {
                        if ((prop.GetSetMethod(true)?.IsPublic ?? false) && prop.CanRead && (prop.GetGetMethod(true)?.IsPublic ?? false))
                        {
                            try { it[prop.Name] = SafeSerializeValue(prop.GetValue(item)); }
                            catch (Exception ex) { Logger.Debug($"Failed to read item property {prop.Name}: {ex.Message}"); }
                        }
                    }

                    // Expand WaitLoopData progress fields for items with Data property
                    try
                    {
                        var dataProperty = item.GetType().GetProperty("Data", BindingFlags.Public | BindingFlags.Instance);
                        if (dataProperty != null && dataProperty.CanRead)
                        {
                            var dataValue = dataProperty.GetValue(item);
                            if (dataValue != null)
                            {
                                var dataType = dataValue.GetType();
                                var currentAltProp = dataType.GetProperty("CurrentAltitude", BindingFlags.Public | BindingFlags.Instance);
                                var targetAltProp = dataType.GetProperty("TargetAltitude", BindingFlags.Public | BindingFlags.Instance);
                                var expectedTimeProp = dataType.GetProperty("ExpectedTime", BindingFlags.Public | BindingFlags.Instance);
                                var comparatorProp = dataType.GetProperty("Comparator", BindingFlags.Public | BindingFlags.Instance);

                                if (currentAltProp != null && targetAltProp != null && expectedTimeProp != null && comparatorProp != null)
                                {
                                    it["CurrentAltitude"] = currentAltProp.GetValue(dataValue);
                                    it["TargetAltitude"] = targetAltProp.GetValue(dataValue);
                                    it["ExpectedTime"] = expectedTimeProp.GetValue(dataValue);
                                    it["Comparator"] = comparatorProp.GetValue(dataValue)?.ToString();
                                }
                            }
                        }
                    }
                    catch { /* Silently ignore if Data property doesn't exist or can't be accessed */ }

                    // RemainingTime is read-only for TimeSpanCondition/TimeCondition items
                    if (item is TimeSpanCondition timeSpanItem)
                    {
                        it["RemainingTime"] = timeSpanItem.RemainingTime.ToString(@"hh\:mm\:ss");
                    }
                    else if (item is TimeCondition timeItem)
                    {
                        it["RemainingTime"] = timeItem.RemainingTime.ToString(@"hh\:mm\:ss");
                    }

                    // SafetyMonitorCondition - IsSafe has a protected setter
                    if (item is SafetyMonitorCondition safetyItem)
                    {
                        it["IsSafe"] = safetyItem.IsSafe;
                    }

                    result.Add(it);
                }
                // A concurrent change of a nested collection must reach RetryOnConcurrentModification;
                // swallowing it here would silently drop the whole subtree from the response.
                catch (Exception ex) when (ex is not InvalidOperationException)
                {
                    Logger.Error(ex);
                }
            }

            return result;
        }

        private static void WriteSequenceResponseData(IHttpContext context, object data)
        {
            context.Response.ContentType = "application/json";

            var settings = new JsonSerializerSettings
            {
                MaxDepth = 100,
                ReferenceLoopHandling = ReferenceLoopHandling.Ignore
            };

            string json = JsonConvert.SerializeObject(data, settings);
            var bytes = System.Text.Encoding.UTF8.GetBytes(json);
            context.Response.ContentLength64 = bytes.Length;

            using (var writer = new StreamWriter(context.Response.OutputStream))
            {
                writer.Write(json);
            }
        }
    }
}
