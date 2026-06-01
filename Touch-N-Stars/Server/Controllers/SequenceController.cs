using EmbedIO;
using EmbedIO.Routing;
using EmbedIO.WebApi;
using NINA.Core.Model.Equipment;
using NINA.Core.Utility;
using NINA.Equipment.Interfaces;
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
        }

        // Single global dictionary for all IDs - STATIC so it persists across HTTP requests
        private static Dictionary<string, TrackedObject> objectIdMap = new();
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
        public SequenceListResponse ListSequenceFiles()
        {
            try
            {
                var sequenceFiles = new List<SequenceFileInfo>();
                var searchDirectories = new List<string>();

                // Try to get the profile sequence directory
                var profileDir = TouchNStars.Mediators?.Profile?.ActiveProfile?.SequenceSettings?.DefaultSequenceFolder;
                if (!string.IsNullOrEmpty(profileDir) && Directory.Exists(profileDir))
                {
                    searchDirectories.Add(profileDir);
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
                var container = converter.Deserialize(jsonContent, filePath);

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

                    List<Hashtable> sequenceData =
                    [
                        new Hashtable() {
                            { "Id", GetOrCreateId(mainContainer, ObjectType.Item) },
                            { "GlobalTriggers", getTriggers((SequenceContainer)mainContainer) }
                        },
                        .. getSequenceRecursively(mainContainer),
                    ]; // Global triggers

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

        /// <summary>
        /// <summary>
        /// POST /api/sequence/move - Move a sequence item, trigger, or condition before or after a target (by ID)
        /// id: ID of the object to move (item, trigger, or condition)
        /// targetId: ID of the target object to move before/after
        /// insertAfter: if true, move after target; if false, move before (default: true)
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

                    bool targetIsItem = targetObject is ISequenceItem;
                    bool targetIsTrigger = targetObject is ISequenceTrigger;
                    bool targetIsCondition = targetObject is ISequenceCondition;

                    // Ensure both objects are the same type
                    if ((isItem && !targetIsItem) || (isTrigger && !targetIsTrigger) || (isCondition && !targetIsCondition))
                    {
                        HttpContext.Response.StatusCode = 400;
                        return new ApiResponse
                        {
                            Success = false,
                            Error = "Cannot move objects of different types",
                        };
                    }

                    // Handle items
                    if (isItem)
                    {
                        return MoveItem((ISequenceItem)objectToMove, (ISequenceItem)targetObject, insertAfter);
                    }
                    // Handle triggers
                    else if (isTrigger)
                    {
                        return MoveTrigger((ISequenceTrigger)objectToMove, (ISequenceTrigger)targetObject, insertAfter);
                    }
                    // Handle conditions
                    else if (isCondition)
                    {
                        return MoveCondition((ISequenceCondition)objectToMove, (ISequenceCondition)targetObject, insertAfter);
                    }
                    else
                    {
                        HttpContext.Response.StatusCode = 400;
                        return new ApiResponse
                        {
                            Success = false,
                            Error = "Unknown object type",
                        };
                    }
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
                    // Type not found in factory - try reflection for full names
                    try
                    {
                        var resolvedType = Type.GetType(type);
                        if (resolvedType != null)
                        {
                            // Try to instantiate and determine what it is
                            var instance = Activator.CreateInstance(resolvedType);

                            if (instance is ISequenceItem)
                            {
                                return AddSequenceItem(targetId, type, insertAfter);
                            }
                            else if (instance is ISequenceTrigger)
                            {
                                return AddTrigger(targetId, type, insertAfter);
                            }
                            else if (instance is ISequenceCondition)
                            {
                                return AddCondition(targetId, type, insertAfter);
                            }
                        }
                    }
                    catch { /* Fall through to error */ }

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
        /// <summary>
        /// Move a sequence item before or after a target item
        /// </summary>
        private ApiResponse MoveItem(ISequenceItem itemToMove, ISequenceItem targetItem, bool? insertAfter)
        {
            try
            {
                bool shouldInsertAfter = insertAfter ?? true;

                // Get index paths for both items
                var itemIndexPath = CalculateIndexPathForItem(itemToMove);
                var targetIndexPath = CalculateIndexPathForItem(targetItem);

                if (string.IsNullOrEmpty(itemIndexPath) || string.IsNullOrEmpty(targetIndexPath))
                {
                    HttpContext.Response.StatusCode = 404;
                    return new ApiResponse
                    {
                        Success = false,
                        Error = "Could not find position of item or target in sequence",
                    };
                }

                var itemIndices = itemIndexPath.Split(',').Select(s => int.Parse(s.Trim(), System.Globalization.CultureInfo.InvariantCulture)).ToList();
                var targetIndices = targetIndexPath.Split(',').Select(s => int.Parse(s.Trim(), System.Globalization.CultureInfo.InvariantCulture)).ToList();

                // Items must be in the same parent container to move
                if (itemIndices.Count != targetIndices.Count)
                {
                    HttpContext.Response.StatusCode = 400;
                    return new ApiResponse
                    {
                        Success = false,
                        Error = "Items must be in the same container hierarchy to move",
                    };
                }

                // Check if they're in the same parent
                for (int i = 0; i < itemIndices.Count - 1; i++)
                {
                    if (itemIndices[i] != targetIndices[i])
                    {
                        HttpContext.Response.StatusCode = 400;
                        return new ApiResponse
                        {
                            Success = false,
                            Error = "Items must be in the same container to move",
                        };
                    }
                }

                // Navigate to the parent container
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
                for (int i = 0; i < itemIndices.Count - 1; i++)
                {
                    var idx = itemIndices[i];
                    if (idx < 0 || idx >= parentContainer.Items.Count)
                    {
                        HttpContext.Response.StatusCode = 404;
                        return new ApiResponse
                        {
                            Success = false,
                            Error = "Invalid container path",
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
                            Error = "Item at container path is not a container",
                        };
                    }
                }

                int currentIndex = itemIndices[itemIndices.Count - 1];
                int targetIndex = targetIndices[targetIndices.Count - 1] + (shouldInsertAfter ? 1 : 0);

                // Adjust targetIndex if moving from before the target
                // When we remove from currentIndex, indices shift down, affecting the target position
                if (currentIndex < targetIndex)
                {
                    targetIndex--;
                }

                // Perform the move
                Application.Current.Dispatcher.Invoke(() =>
                {
                    if (parentContainer is SequenceContainer seqContainer)
                    {
                        if (currentIndex >= 0 && currentIndex < seqContainer.Items.Count &&
                            targetIndex >= 0 && targetIndex <= seqContainer.Items.Count &&
                            currentIndex != targetIndex)
                        {
                            seqContainer.MoveWithinIntoSequenceBlocks(currentIndex, targetIndex);
                        }
                    }
                });

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
                Logger.Error($"Error moving item: {ex}");
                HttpContext.Response.StatusCode = 500;
                return new ApiResponse
                {
                    Success = false,
                    Error = $"Failed to move item: {ex.Message}",
                    StatusCode = 500,
                    Type = "Error"
                };
            }
        }

        /// <summary>
        /// Move a trigger before or after a target trigger within the same container
        /// </summary>
        private ApiResponse MoveTrigger(ISequenceTrigger triggerToMove, ISequenceTrigger targetTrigger, bool? insertAfter)
        {
            try
            {
                bool shouldInsertAfter = insertAfter ?? true;

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

                // Find the container that holds both triggers
                var triggerContainer = FindTriggerContainer(mainContainer, triggerToMove);
                if (triggerContainer == null)
                {
                    HttpContext.Response.StatusCode = 404;
                    return new ApiResponse
                    {
                        Success = false,
                        Error = "Trigger to move not found in sequence",
                    };
                }

                var targetContainer = FindTriggerContainer(mainContainer, targetTrigger);
                if (targetContainer == null)
                {
                    HttpContext.Response.StatusCode = 404;
                    return new ApiResponse
                    {
                        Success = false,
                        Error = "Target trigger not found in sequence",
                    };
                }

                // Both triggers must be in the same container
                if (triggerContainer != targetContainer)
                {
                    HttpContext.Response.StatusCode = 400;
                    return new ApiResponse
                    {
                        Success = false,
                        Error = "Triggers must be in the same container to move",
                    };
                }

                if (triggerContainer is SequenceContainer seqContainer)
                {
                    var triggersList = seqContainer.Triggers;
                    int currentIndex = triggersList.IndexOf(triggerToMove);
                    int targetIndex = triggersList.IndexOf(targetTrigger);

                    if (currentIndex < 0 || targetIndex < 0)
                    {
                        HttpContext.Response.StatusCode = 404;
                        return new ApiResponse
                        {
                            Success = false,
                            Error = "Could not determine trigger positions",
                        };
                    }

                    if (currentIndex == targetIndex)
                    {
                        HttpContext.Response.StatusCode = 200;
                        return new ApiResponse
                        {
                            Success = true,
                            Error = null,
                            StatusCode = 200,
                            Type = "Success"
                        };
                    }

                    // Calculate new index
                    int newIndex = targetIndex + (shouldInsertAfter ? 1 : 0);

                    // Adjust if moving before target
                    if (currentIndex < newIndex)
                    {
                        newIndex--;
                    }

                    // Clamp to valid range
                    newIndex = Math.Max(0, Math.Min(newIndex, triggersList.Count - 1));

                    // Perform the move
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        triggersList.RemoveAt(currentIndex);
                        triggersList.Insert(newIndex, triggerToMove);
                    });

                    HttpContext.Response.StatusCode = 200;
                    return new ApiResponse
                    {
                        Success = true,
                        Error = null,
                        StatusCode = 200,
                        Type = "Success"
                    };
                }
                else
                {
                    HttpContext.Response.StatusCode = 400;
                    return new ApiResponse
                    {
                        Success = false,
                        Error = "Invalid container type",
                    };
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Error moving trigger: {ex}");
                HttpContext.Response.StatusCode = 500;
                return new ApiResponse
                {
                    Success = false,
                    Error = $"Failed to move trigger: {ex.Message}",
                    StatusCode = 500,
                    Type = "Error"
                };
            }
        }

        /// <summary>
        /// Move a condition before or after a target condition within the same container
        /// </summary>
        private ApiResponse MoveCondition(ISequenceCondition conditionToMove, ISequenceCondition targetCondition, bool? insertAfter)
        {
            try
            {
                bool shouldInsertAfter = insertAfter ?? true;

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

                // Find the container that holds both conditions
                var conditionContainer = FindConditionContainer(mainContainer, conditionToMove);
                if (conditionContainer == null)
                {
                    HttpContext.Response.StatusCode = 404;
                    return new ApiResponse
                    {
                        Success = false,
                        Error = "Condition to move not found in sequence",
                    };
                }

                var targetContainer = FindConditionContainer(mainContainer, targetCondition);
                if (targetContainer == null)
                {
                    HttpContext.Response.StatusCode = 404;
                    return new ApiResponse
                    {
                        Success = false,
                        Error = "Target condition not found in sequence",
                    };
                }

                // Both conditions must be in the same container
                if (conditionContainer != targetContainer)
                {
                    HttpContext.Response.StatusCode = 400;
                    return new ApiResponse
                    {
                        Success = false,
                        Error = "Conditions must be in the same container to move",
                    };
                }

                if (conditionContainer is SequenceContainer seqContainer)
                {
                    var conditionsList = seqContainer.Conditions;
                    int currentIndex = conditionsList.IndexOf(conditionToMove);
                    int targetIndex = conditionsList.IndexOf(targetCondition);

                    if (currentIndex < 0 || targetIndex < 0)
                    {
                        HttpContext.Response.StatusCode = 404;
                        return new ApiResponse
                        {
                            Success = false,
                            Error = "Could not determine condition positions",
                        };
                    }

                    if (currentIndex == targetIndex)
                    {
                        HttpContext.Response.StatusCode = 200;
                        return new ApiResponse
                        {
                            Success = true,
                            Error = null,
                            StatusCode = 200,
                            Type = "Success"
                        };
                    }

                    // Calculate new index
                    int newIndex = targetIndex + (shouldInsertAfter ? 1 : 0);

                    // Adjust if moving before target
                    if (currentIndex < newIndex)
                    {
                        newIndex--;
                    }

                    // Clamp to valid range
                    newIndex = Math.Max(0, Math.Min(newIndex, conditionsList.Count - 1));

                    // Perform the move
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        conditionsList.RemoveAt(currentIndex);
                        conditionsList.Insert(newIndex, conditionToMove);
                    });

                    HttpContext.Response.StatusCode = 200;
                    return new ApiResponse
                    {
                        Success = true,
                        Error = null,
                        StatusCode = 200,
                        Type = "Success"
                    };
                }
                else
                {
                    HttpContext.Response.StatusCode = 400;
                    return new ApiResponse
                    {
                        Success = false,
                        Error = "Invalid container type",
                    };
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Error moving condition: {ex}");
                HttpContext.Response.StatusCode = 500;
                return new ApiResponse
                {
                    Success = false,
                    Error = $"Failed to move condition: {ex.Message}",
                    StatusCode = 500,
                    Type = "Error"
                };
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

                    // If not found in factory, try reflection as fallback for full type names
                    if (templateItem == null && itemType.Contains("."))
                    {
                        try
                        {
                            var resolvedType = Type.GetType(itemType);
                            if (resolvedType != null && typeof(ISequenceItem).IsAssignableFrom(resolvedType))
                            {
                                templateItem = Activator.CreateInstance(resolvedType) as ISequenceItem;
                            }
                        }
                        catch { /* Fall through to error */ }
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

                    // If not found in factory, try reflection as fallback for full type names
                    if (templateTrigger == null && triggerType.Contains("."))
                    {
                        try
                        {
                            var resolvedType = Type.GetType(triggerType);
                            if (resolvedType != null && typeof(ISequenceTrigger).IsAssignableFrom(resolvedType))
                            {
                                templateTrigger = Activator.CreateInstance(resolvedType) as ISequenceTrigger;
                            }
                        }
                        catch { /* Fall through to error */ }
                    }

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
                    objectIdMap.Remove(id);

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

                    // If not found in factory, try reflection as fallback for full type names
                    if (templateCondition == null && conditionType.Contains("."))
                    {
                        try
                        {
                            var resolvedType = Type.GetType(conditionType);
                            if (resolvedType != null && typeof(ISequenceCondition).IsAssignableFrom(resolvedType))
                            {
                                templateCondition = Activator.CreateInstance(resolvedType) as ISequenceCondition;
                            }
                        }
                        catch { /* Fall through to error */ }
                    }

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

                    // Ensure directory exists
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
                            objectInfo.Add(prop.Name, SafeSerializeValue(prop.GetValue(obj)));
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
                    objectInfo.Add("Items", getSequenceRecursively(featureContainer));
                }

                // For all containers, include triggers and conditions
                if (obj is ISequenceContainer container)
                {
                    var seqContainer = obj as SequenceContainer;
                    if (seqContainer != null)
                    {
                        objectInfo.Add("Triggers", getTriggers(seqContainer));
                        objectInfo.Add("Conditions", getConditions(seqContainer));
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

                        // Convert and set the value
                        object convertedValue = ConvertValue(value, finalProp.PropertyType);
                        finalProp.SetValue(currentObj, convertedValue);

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

                if (targetType == typeof(double))
                    return double.Parse(value, System.Globalization.CultureInfo.InvariantCulture);

                if (targetType == typeof(decimal))
                    return decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture);

                if (targetType == typeof(long))
                    return long.Parse(value, System.Globalization.CultureInfo.InvariantCulture);

                if (targetType == typeof(float))
                    return float.Parse(value, System.Globalization.CultureInfo.InvariantCulture);

                if (targetType.IsEnum)
                    return Enum.Parse(targetType, value);

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

        /// <summary>
        /// Helper method to safely serialize a value to something JSON-serializable
        /// </summary>
        private static object SafeSerializeValue(object value)
        {
            if (value == null)
                return null;

            // Handle primitives and common types
            var type = value.GetType();
            if (type.IsPrimitive || type == typeof(string) || type == typeof(decimal) ||
                type == typeof(DateTime) || type == typeof(TimeSpan) || type == typeof(Guid))
            {
                return value;
            }

            // Handle enums
            if (type.IsEnum)
                return value.ToString();

            // Handle booleans (already handled above but just to be explicit)
            if (type == typeof(bool))
                return value;

            // Handle ISequenceContainer (e.g. BeforeFlipActions/AfterFlipActions on
            // ProgrammableMeridianFlipTrigger) using the dedicated sequence machinery to avoid
            // infinite recursion through IEnumerable + [JsonProperty] reflection.
            if (value is SequenceContainer seqContainer)
            {
                var containerTable = new Hashtable
                {
                    { "Items", getSequenceRecursively(seqContainer) },
                    { "Conditions", getConditions(seqContainer) },
                    { "Triggers", getTriggers(seqContainer) }
                };
                return containerTable;
            }

            // Handle collections/IEnumerable (convert to array)
            if (value is System.Collections.IEnumerable enumerable && !(value is string))
            {
                try
                {
                    var list = new List<object>();
                    foreach (var item in enumerable)
                    {
                        // Recursively serialize each item
                        list.Add(SafeSerializeValue(item));
                    }
                    return list;
                }
                catch { /* Fall through to other handlers */ }
            }

            // Handle IWritableSwitch (e.g. PowerBoxWritableSwitch) - serialize all switch fields
            if (value is IWritableSwitch writableSwitch)
            {
                return new Hashtable
                {
                    { "Id", writableSwitch.Id },
                    { "Name", writableSwitch.Name },
                    { "Description", writableSwitch.Description },
                    { "Value", writableSwitch.Value },
                    { "Minimum", writableSwitch.Minimum },
                    { "Maximum", writableSwitch.Maximum },
                    { "StepSize", writableSwitch.StepSize },
                    { "TargetValue", writableSwitch.TargetValue },
                };
            }

            // Handle ISwitch (read-only switches)
            if (value is ISwitch sw)
            {
                return new Hashtable
                {
                    { "Id", sw.Id },
                    { "Name", sw.Name },
                    { "Description", sw.Description },
                    { "Value", sw.Value },
                };
            }

            // Handle IDateTimeProvider - serialize as {Name, FullTypeName}
            if (value is NINA.Sequencer.Utility.DateTimeProvider.IDateTimeProvider dtProvider)
            {
                return new Hashtable
                {
                    { "Name", dtProvider.Name },
                    { "FullTypeName", dtProvider.GetType().FullName }
                };
            }

            // Special handling for Expression objects
            if (type.Name == "Expression")
            {
                // Try multiple properties to extract the actual expression value
                var props = new[] { "ExpressionString", "RawExpression", "Definition", "Expression" };
                foreach (var propName in props)
                {
                    var propValue = type.GetProperty(propName)?.GetValue(value) as string;
                    if (!string.IsNullOrEmpty(propValue))
                        return propValue;
                }

                // Try to get the Value property
                var exprValue = type.GetProperty("Value")?.GetValue(value);
                if (exprValue != null && !(exprValue is string str && str.StartsWith("Undefined")))
                    return SafeSerializeValue(exprValue);

                // If all else fails, check if there's a meaningful ToString()
                var strValue = value.ToString();
                // Return it as-is; if it's "Undefined in  (with Validator)" that reflects the actual state
                return strValue;
            }

            // Generic fallback for complex types: reflect over [JsonProperty]-annotated properties.
            // This handles InputCoordinates, InputTopocentricCoordinates, InputTarget, and any
            // future NINA type without needing explicit case-by-case handling here.
            if (type.IsClass || (type.IsValueType && !type.IsEnum))
            {
                var jsonProps = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .Where(p => p.CanRead
                             && p.GetIndexParameters().Length == 0
                             && p.GetCustomAttribute<JsonPropertyAttribute>() != null);

                if (jsonProps.Any())
                {
                    var dict = new Hashtable();
                    foreach (var prop in jsonProps)
                    {
                        try { dict[prop.Name] = SafeSerializeValue(prop.GetValue(value)); }
                        catch { /* skip properties that throw */ }
                    }
                    return dict;
                }
            }

            // Fallback: return string representation for unknown complex types
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
                    return new ApiResponse { Success = false, Error = "Object not found", StatusCode = 400, Type = "Error" };
                }

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
                            // Match WPF ResetProgressCommand behavior: reset only this item (and its
                            // children if it is a container), then cascade the status change upward
                            // to parent containers.  Do NOT reset subsequent siblings.
                            if (item is ISequenceContainer container)
                            {
                                container.ResetAll();
                            }
                            item.ResetProgressCascaded();
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

                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        mainContainer.DetachCommand?.Execute(null);
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
                    // Get the currently running items using the built-in tracking method
                    var runningItems = mainContainer.GetCurrentRunningItems();

                    if (runningItems == null || runningItems.Count == 0)
                    {
                        HttpContext.Response.StatusCode = 200;
                        return new ApiResponse { Success = true, Error = null, StatusCode = 200, Type = "NoCurrentItem" };
                    }

                    // Get the first (usually only) currently running item
                    var currentItem = runningItems.FirstOrDefault();
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
            return $"id_{++idCounter}";
        }

        /// <summary>
        /// Get or generate an ID for an object, reusing existing IDs from the map
        /// </summary>
        private static string GetOrCreateId(object obj, ObjectType type)
        {
            // Search for existing ID of this object in the map
            foreach (var kvp in objectIdMap)
            {
                if (kvp.Value.Value == obj && kvp.Value.Type == type)
                {
                    return kvp.Key;
                }
            }

            // If not found, generate new ID and track it
            string newId = GenerateId();
            objectIdMap[newId] = new TrackedObject { Value = obj, Type = type };
            return newId;
        }

        /// <summary>
        /// Reset ID counter and map - call when a new sequence is loaded or cleared
        /// </summary>
        private static void ResetIdCounterAndMap()
        {
            objectIdMap.Clear();
            idCounter = 0;
            lastLoadedSequence = null;
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
            // Check if this item is already tracked
            foreach (var kvp in objectIdMap)
            {
                if (kvp.Value.Type == ObjectType.Item && ReferenceEquals(kvp.Value.Value, item))
                    return kvp.Key;  // Return existing ID
            }

            // Not found, generate new ID
            var id = GenerateId();
            objectIdMap[id] = new TrackedObject { Value = item, Type = ObjectType.Item };
            return id;
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
            var itemId = objectIdMap.FirstOrDefault(kv => kv.Value.Value == item).Key;
            if (!string.IsNullOrEmpty(itemId))
            {
                objectIdMap.Remove(itemId);
            }

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
                        var triggerId = objectIdMap.FirstOrDefault(kv => kv.Value.Value == trigger).Key;
                        if (!string.IsNullOrEmpty(triggerId))
                        {
                            objectIdMap.Remove(triggerId);
                        }
                    }

                    foreach (var condition in seqContainer.Conditions)
                    {
                        var conditionId = objectIdMap.FirstOrDefault(kv => kv.Value.Value == condition).Key;
                        if (!string.IsNullOrEmpty(conditionId))
                        {
                            objectIdMap.Remove(conditionId);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Track a trigger by ID for later lookup - reuses existing ID if already tracked
        /// </summary>
        private string TrackTrigger(ISequenceTrigger trigger)
        {
            // Check if this trigger is already tracked
            foreach (var kvp in objectIdMap)
            {
                if (kvp.Value.Type == ObjectType.Trigger && ReferenceEquals(kvp.Value.Value, trigger))
                    return kvp.Key;  // Return existing ID
            }

            // Not found, generate new ID
            var id = GenerateId();
            objectIdMap[id] = new TrackedObject { Value = trigger, Type = ObjectType.Trigger };
            return id;
        }

        /// <summary>
        /// Track a condition by ID for later lookup - reuses existing ID if already tracked
        /// </summary>
        private string TrackCondition(ISequenceCondition condition)
        {
            // Check if this condition is already tracked
            foreach (var kvp in objectIdMap)
            {
                if (kvp.Value.Type == ObjectType.Condition && ReferenceEquals(kvp.Value.Value, condition))
                    return kvp.Key;  // Return existing ID
            }

            // Not found, generate new ID
            var id = GenerateId();
            objectIdMap[id] = new TrackedObject { Value = condition, Type = ObjectType.Condition };
            return id;
        }

        /// <summary>
        /// Find an item by its tracked ID
        /// </summary>
        private ISequenceItem FindItemById(string id)
        {
            return objectIdMap.TryGetValue(id, out var tracked) && tracked.Type == ObjectType.Item
                ? tracked.Value as ISequenceItem
                : null;
        }

        /// <summary>
        /// Get the type of an object by its ID
        /// </summary>
        private ObjectType? GetObjectType(string id)
        {
            return objectIdMap.TryGetValue(id, out var tracked) ? tracked.Type : null;
        }

        /// <summary>
        /// Find any object (item, trigger, or condition) by its ID
        /// </summary>
        private object FindObjectById(string id)
        {
            return objectIdMap.TryGetValue(id, out var tracked) ? tracked.Value : null;
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
            var triggerId = objectIdMap.FirstOrDefault(kv => kv.Value.Value == trigger).Key;
            if (!string.IsNullOrEmpty(triggerId))
            {
                objectIdMap.Remove(triggerId);
            }
        }

        /// <summary>
        /// Remove tracking for a condition
        /// </summary>
        private void UntrackCondition(ISequenceCondition condition)
        {
            var conditionId = objectIdMap.FirstOrDefault(kv => kv.Value.Value == condition).Key;
            if (!string.IsNullOrEmpty(conditionId))
            {
                objectIdMap.Remove(conditionId);
            }
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
                            triggerTable.Add(prop.Name, SafeSerializeValue(prop.GetValue(trigger)));
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
                            ctable.Add(prop.Name, SafeSerializeValue(prop.GetValue(condition)));
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
                            it.Add(prop.Name, SafeSerializeValue(prop.GetValue(item)));
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
                catch (Exception ex)
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
