﻿#region Copyright
//
// DotNetNuke® - http://www.dnnsoftware.com
// Copyright (c) 2002-2017
// by DotNetNuke Corporation
//
// Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated
// documentation files (the "Software"), to deal in the Software without restriction, including without limitation
// the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and
// to permit persons to whom the Software is furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in all copies or substantial portions
// of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED
// TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL
// THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF
// CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
// DEALINGS IN THE SOFTWARE.
#endregion

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Web.Caching;
using Dnn.ExportImport.Components.Common;
using Dnn.ExportImport.Components.Controllers;
using Dnn.ExportImport.Components.Dto;
using Dnn.ExportImport.Components.Entities;
using Dnn.ExportImport.Components.Interfaces;
using Dnn.ExportImport.Components.Models;
using Dnn.ExportImport.Components.Repository;
using DotNetNuke.Common;
using DotNetNuke.Framework.Reflections;
using DotNetNuke.Instrumentation;
using DotNetNuke.Services.Cache;
using DotNetNuke.Services.Scheduling;
using Newtonsoft.Json;

namespace Dnn.ExportImport.Components.Engines
{
    public class ExportImportEngine
    {
        private static readonly ILog Logger = LoggerSource.Instance.GetLogger(typeof(ExportImportEngine));

        private const StringComparison IgnoreCaseComp = StringComparison.InvariantCultureIgnoreCase;
        private const double MaxTimeToRunJob = 90; // in seconds

        private static readonly string DbFolder;

        static ExportImportEngine()
        {
            DbFolder = Globals.ApplicationMapPath + Constants.ExportFolder;
            if (!Directory.Exists(DbFolder))
            {
                Directory.CreateDirectory(DbFolder);
            }
        }

        private readonly Stopwatch _stopWatch = Stopwatch.StartNew();

        public int ProgressPercentage { get; private set; } = 1;

        public ExportImportResult Export(ExportImportJob exportJob, ScheduleHistoryItem scheduleHistoryItem)
        {
            var result = new ExportImportResult
            {
                JobId = exportJob.JobId,
                Status = JobStatus.DoneFailure,
            };

            var exportDto = JsonConvert.DeserializeObject<ExportDto>(exportJob.JobObject);
            if (exportDto == null)
            {
                exportJob.CompletedOnDate = DateTime.UtcNow;
                exportJob.JobStatus = JobStatus.DoneFailure;
                return result;
            }

            if (!exportDto.ItemsToExport.Any())
            {
                exportJob.CompletedOnDate = DateTime.UtcNow;
                exportJob.JobStatus = JobStatus.DoneFailure;
                scheduleHistoryItem.AddLogNote("<br/>No items selected for exporting");
                return result;
            }

            var dbName = Path.Combine(DbFolder, exportJob.ExportFile + Constants.ExportDbExt);
            var finfo = new FileInfo(dbName);

            var checkpoints = EntitiesController.Instance.GetJobChekpoints(exportJob.JobId);

            //Delete so we start a fresh export database; only if there is no previous checkpoint exists
            if (checkpoints.Count == 0 && finfo.Exists) finfo.Delete();

            using (var ctx = new ExportImportRepository(dbName))
            using (var cts = new CancellationTokenSource())
            {
                result.AddSummary("Exporting Repository", finfo.Name);
                ctx.AddSingleItem(exportDto);
                result.Status = JobStatus.InProgress;

                // there must be one parent implementor at least for this to work
                var implementors = GetPortableImplementors().ToList();
                var parentServices = implementors.Where(imp => string.IsNullOrEmpty(imp.ParentCategory)).ToList();
                implementors = implementors.Except(parentServices).ToList();
                var nextLevelServices = new List<IPortable2>();
                var includedItems = GetAllCategoriesToInclude(exportDto, implementors);
                var firstLoop = true;
                AddTokenToCache(Util.GetExpImpJobCacheKey(exportJob), cts);

                do
                {
                    if (cts.IsCancellationRequested)
                    {
                        result.Status = JobStatus.Cancelled;
                        break;
                    }

                    foreach (var service in parentServices.OrderBy(x => x.Priority))
                    {
                        if (cts.IsCancellationRequested)
                        {
                            result.Status = JobStatus.Cancelled;
                            break;
                        }

                        if (implementors.Count > 0)
                        {
                            // collect children for next iteration
                            var children = implementors.Where(imp => service.Category.Equals(imp.ParentCategory, IgnoreCaseComp));
                            nextLevelServices.AddRange(children);
                            implementors = implementors.Except(nextLevelServices).ToList();
                        }

                        if ((firstLoop && includedItems.Any(x => x.Equals(service.Category, IgnoreCaseComp))) ||
                            (!firstLoop && includedItems.Any(x => x.Equals(service.ParentCategory, IgnoreCaseComp))))
                        {
                            service.Result = result;
                            service.Repository = ctx;
                            service.CancellationToken = cts.Token;
                            service.CheckPointStageCallback = CheckpointCallback;
                            service.CheckPoint = checkpoints.FirstOrDefault(cp => cp.Category == service.Category)
                                     ?? new ExportImportChekpoint
                                     {
                                         JobId = exportJob.JobId,
                                         Category = service.Category
                                     };
                            CheckpointCallback(service);

                            service.ExportData(exportJob, exportDto);
                            scheduleHistoryItem.AddLogNote("<br/>Exported: " + service.Category);
                        }
                    }

                    firstLoop = false;
                    parentServices = new List<IPortable2>(nextLevelServices);
                    nextLevelServices.Clear();
                    if (implementors.Count > 0 && parentServices.Count == 0)
                    {
                        //WARN: this is a case where there is a broken parnets-children hierarchy
                        //      and/or there are IPortable2 implementations without a known parent.
                        parentServices = implementors;
                        implementors.Clear();
                        scheduleHistoryItem.AddLogNote(
                            "<br/><b>Orphaned services:</b> " + string.Join(",", parentServices.Select(x => x.Category)));
                    }
                } while (parentServices.Count > 0);

                //TODO: zip files when any

                RemoveTokenFromCache(exportJob);
            }

            if (result.Status == JobStatus.InProgress)
                result.Status = JobStatus.DoneSuccess;

            // wait for the file to be flushed as finfo.Length will throw exception
            while (!finfo.Exists) { Thread.Sleep(1); }
            finfo = new FileInfo(finfo.FullName); // refresh to get new size
            result.AddSummary("Exported File Size", Util.FormatSize(finfo.Length));
            exportJob.JobStatus = result.Status;

            return result;
        }

        public ExportImportResult Import(ExportImportJob importJob, ScheduleHistoryItem scheduleHistoryItem)
        {
            var result = new ExportImportResult
            {
                JobId = importJob.JobId,
                Status = JobStatus.DoneFailure,
            };

            var importDto = JsonConvert.DeserializeObject<ImportDto>(importJob.JobObject);
            if (importDto == null)
            {
                importJob.CompletedOnDate = DateTime.UtcNow;
                importJob.JobStatus = JobStatus.DoneFailure;
                return result;
            }

            var dbName = Path.Combine(DbFolder, importDto.FileName);
            var finfo = new FileInfo(dbName);
            if (!finfo.Exists)
            {
                scheduleHistoryItem.AddLogNote("<br/>Import file not found. Name: " + dbName);
                importJob.CompletedOnDate = DateTime.UtcNow;
                importJob.JobStatus = JobStatus.DoneFailure;
                return result;
            }

            result.AddSummary("Importing Repository", finfo.Name);
            result.AddSummary("Imported File Size", Util.FormatSize(finfo.Length));

            using (var ctx = new ExportImportRepository(dbName))
            using (var cts = new CancellationTokenSource())
            {
                var exportedDto = ctx.GetSingleItem<ExportDto>();
                var exportVersion = new Version(exportedDto.SchemaVersion);
                var importVersion = new Version(importDto.SchemaVersion);
                if (importVersion < exportVersion)
                {
                    result.Status = JobStatus.DoneFailure;
                    var msg = $"Exported version ({exportedDto.SchemaVersion}) is newer than import engine version ({importDto.SchemaVersion})";
                    scheduleHistoryItem.AddLogNote("Import NOT Possible");
                    scheduleHistoryItem.AddLogNote("<br/>No items selected for exporting");
                    result.AddSummary("Import NOT Possible", msg);
                }
                else
                {
                    var implementors = GetPortableImplementors().ToList();
                    var parentServices = implementors.Where(imp => string.IsNullOrEmpty(imp.ParentCategory)).ToList();
                    result.Status = JobStatus.InProgress;

                    // there must be one parent implementor at least for this to work
                    implementors = implementors.Except(parentServices).ToList();
                    var nextLevelServices = new List<IPortable2>();
                    var includedItems = GetAllCategoriesToInclude(exportedDto, implementors);
                    var checkpoints = EntitiesController.Instance.GetJobChekpoints(importJob.JobId);
                    var firstLoop = true;
                    AddTokenToCache(Util.GetExpImpJobCacheKey(importJob), cts);

                    do
                    {
                        if (cts.IsCancellationRequested)
                        {
                            result.Status = JobStatus.Cancelled;
                            break;
                        }

                        foreach (var service in parentServices.OrderBy(x => x.Priority))
                        {
                            if (cts.IsCancellationRequested)
                            {
                                result.Status = JobStatus.Cancelled;
                                break;
                            }

                            if (implementors.Count > 0)
                            {
                                // collect children for next iteration
                                var children = implementors.Where(imp => service.Category.Equals(imp.ParentCategory, IgnoreCaseComp));
                                nextLevelServices.AddRange(children);
                                implementors = implementors.Except(nextLevelServices).ToList();
                            }

                            if ((firstLoop && includedItems.Any(x => x.Equals(service.Category, IgnoreCaseComp))) ||
                                (!firstLoop && includedItems.Any(x => x.Equals(service.ParentCategory, IgnoreCaseComp))))
                            {
                                service.Result = result;
                                service.Repository = ctx;
                                service.CancellationToken = cts.Token;
                                service.CheckPointStageCallback = CheckpointCallback;
                                service.CheckPoint = checkpoints.FirstOrDefault(cp => cp.Category == service.Category)
                                         ?? new ExportImportChekpoint
                                         {
                                             JobId = importJob.JobId,
                                             Category = service.Category
                                         };
                                CheckpointCallback(service);

                                service.ImportData(importJob, exportedDto);
                                scheduleHistoryItem.AddLogNote("<br/>Imported: " + service.Category);
                            }
                        }

                        firstLoop = false;
                        parentServices = new List<IPortable2>(nextLevelServices);
                        nextLevelServices.Clear();
                        if (implementors.Count > 0 && parentServices.Count == 0)
                        {
                            //WARN: this is a case where there is a broken parnets-children hierarchy
                            //      and/or there are IPortable2 implementations without a known parent.
                            parentServices = implementors;
                            implementors.Clear();
                            scheduleHistoryItem.AddLogNote(
                                "<br/><b>Orphaned services:</b> " + string.Join(",", parentServices.Select(x => x.Category)));
                        }
                    } while (parentServices.Count > 0);
                }

                RemoveTokenFromCache(importJob);
            }

            if (result.Status == JobStatus.InProgress)
                result.Status = JobStatus.DoneSuccess;

            importJob.JobStatus = result.Status;
            return result;
        }

        /// <summary>
        /// Callback function to provide a checkpoint mechanism for an <see cref="IPortable2"/> implementation.
        /// </summary>
        /// <param name="service">The <see cref="IPortable2"/> implementation</param>
        /// <returns>Treu to stop further <see cref="IPortable2"/> processing; false otherwise</returns>
        private bool CheckpointCallback(IPortable2 service)
        {
            EntitiesController.Instance.UpdateJobChekpoint(service.CheckPoint);
            return _stopWatch.Elapsed.TotalSeconds > MaxTimeToRunJob;
        }

        private static void AddTokenToCache(string jobCacheKey, CancellationTokenSource cts)
        {
            // We add only a simple item to the cache; just a string and not the token
            // itself as this might not work with out-of-process cache. The captured cts
            // will be operated upon when the item is removed from the cache.
            // Note: if "clear cache" is called, this will be triggered and job cancelled.
            CachingProvider.Instance().Insert(jobCacheKey, "ExportImportCT", (DNNCacheDependency)null,
                Cache.NoAbsoluteExpiration, Cache.NoSlidingExpiration, CacheItemPriority.NotRemovable,
                (key, value, reason) =>
                {
                    try
                    {
                        if (!cts.IsCancellationRequested)
                        {
                            if (Logger.IsDebugEnabled)
                                Logger.Debug("Cancellation token removed from the cache; job will be cancelled.");
                            //TODO: check why this method gets called from HttpRuntime disposing thread?
                            //cts.Cancel();
                            //System.Diagnostics.Trace.WriteLine("Removed cache key: " + key);
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Error("Export/Import cache token cancel error. " + ex.Message);
                    }
                });
        }

        private static void RemoveTokenFromCache(ExportImportJob job)
        {
            CachingProvider.Instance().Remove(Util.GetExpImpJobCacheKey(job));
        }

        private static IEnumerable<IPortable2> GetPortableImplementors()
        {
            var typeLocator = new TypeLocator();
            var types = typeLocator.GetAllMatchingTypes(
                t => t != null && t.IsClass && !t.IsAbstract && t.IsVisible &&
                     typeof(IPortable2).IsAssignableFrom(t));

            foreach (var type in types)
            {
                IPortable2 portable2Type;
                try
                {
                    portable2Type = Activator.CreateInstance(type) as IPortable2;
                }
                catch (Exception e)
                {
                    Logger.ErrorFormat("Unable to create {0} while calling IPortable2 implementors. {1}",
                                       type.FullName, e.Message);
                    portable2Type = null;
                }

                if (portable2Type != null)
                {
                    yield return portable2Type;
                }
            }
        }

        private static HashSet<string> GetAllCategoriesToInclude(ExportDto exportDto, List<IPortable2> implementors)
        {
            // add all child items
            var includedItems = new HashSet<string>(StringComparer.InvariantCultureIgnoreCase);
            foreach (var name in exportDto.ItemsToExport)
            {
                includedItems.Add(name);
                foreach (var impl in implementors)
                {
                    if (name.Equals(impl.ParentCategory, IgnoreCaseComp))
                        includedItems.Add(impl.Category);
                }
            }

            if (exportDto.Pages.Length > 0)
                includedItems.Add(Constants.Category_Pages);

            // must be included always when there is at least one other object to process
            if (includedItems.Count > 0)
                includedItems.Add(Constants.Category_Portal);

            return includedItems;
        }
    }
}