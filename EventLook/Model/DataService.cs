﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace EventLook.Model;

public interface IDataService
{
    Task<int> ReadEvents(LogSource eventSource, DateTime fromTime, DateTime toTime, bool readFromNew, IProgress<ProgressInfo> progress);
    void Cancel();
    /// <summary>
    /// Subscribes to events in an event log channel. The caller will be reported whenever a new event comes in.
    /// </summary>
    /// <param name="eventSource"></param>
    /// <param name="handler"></param>
    /// <returns>True if success.</returns>
    bool SubscribeEvents(LogSource eventSource, IProgress<ProgressInfo> progress);
    void UnsubscribeEvents();
}
public class DataService : IDataService
{
    private CancellationTokenSource cts;
    public async Task<int> ReadEvents(LogSource eventSource, DateTime fromTime, DateTime toTime, bool readFromNew, IProgress<ProgressInfo> progress)
    {
        using (cts = new CancellationTokenSource())
        {
            // Event records to be sent to the ViewModel
            var eventRecords = new List<EventRecord>();
            string errMsg = "";
            int count = 0;
            bool isFirst = true;
            try
            {
                string sQuery = string.Format(" *[System[TimeCreated[@SystemTime > '{0}' and @SystemTime <= '{1}']]]",
                    fromTime.ToUniversalTime().ToString("o"),
                    toTime.ToUniversalTime().ToString("o"));

                var elQuery = new EventLogQuery(eventSource.Path, eventSource.PathType, sQuery)
                {
                    ReverseDirection = readFromNew
                };
                var reader = new EventLogReader(elQuery);
                var eventRecord = reader.ReadEvent();
                Debug.WriteLine("Begin Reading");
                await Task.Run(() =>
                {
                    for (; eventRecord != null; eventRecord = reader.ReadEvent())
                    {
                        cts.Token.ThrowIfCancellationRequested();

                        eventRecords.Add(eventRecord);
                        ++count;
                        if (count % 100 == 0)
                        {
                            var info = new ProgressInfo(eventRecords.ConvertAll(e => new EventItem(e)), isComplete: false, isFirst);
                            cts.Token.ThrowIfCancellationRequested();
                            progress.Report(info);
                            isFirst = false;
                            eventRecords.Clear();
                        }
                    }
                });
            }
            catch (OperationCanceledException ex)
            {
                errMsg = ex.Message;
            }
            catch (EventLogNotFoundException ex)
            {
                errMsg = ex.Message;
            }
            catch (EventLogException ex)
            {
                errMsg = ex.Message;
            }
            catch (UnauthorizedAccessException)
            {
                errMsg = "Unauthorized access to the channel. Try Run as Administrator.";
            }
            finally
            {
                var info_comp = new ProgressInfo(eventRecords.ConvertAll(e => new EventItem(e)), isComplete: true, isFirst, errMsg);
                progress.Report(info_comp);
                Debug.WriteLine("End Reading");
            }
            return count;
        }
    }
    public void Cancel()
    {
        cts.Cancel();
    }

    private IProgress<ProgressInfo> progress = null;
    private EventLogWatcher watcher = null;
    public bool SubscribeEvents(LogSource source, IProgress<ProgressInfo> progress)
    {
        if (source.PathType == PathType.FilePath)
            return false;

        this.progress = progress;

        try
        {
            UnsubscribeEvents();
            watcher = new EventLogWatcher(new EventLogQuery(source.Path, PathType.LogName, "*"));
            watcher.EventRecordWritten += new EventHandler<EventRecordWrittenEventArgs>(EventRecordWrittenHandler);
            watcher.Enabled = true;
            return true;
        }
        catch (EventLogException)
        {
            return false;
        }
    }
    public void UnsubscribeEvents()
    {
        if (watcher != null)
        {
            watcher.Enabled = false;
            watcher.Dispose();
            watcher = null;
        }
    }
    private void EventRecordWrittenHandler(object sender, EventRecordWrittenEventArgs arg)
    {
        if (arg.EventRecord == null)
            return;
        try
        {
            var item = new EventItem(arg.EventRecord);
            var info = new ProgressInfo(new List<EventItem> { item }, isComplete: true, isFirst: true);
            progress?.Report(info);
        }
        catch (Exception ex)
        {
            progress?.Report(new ProgressInfo(new List<EventItem>(), isComplete: true, isFirst: true, ex.Message));
        }
    }

    /// <summary>
    /// Gets computer name from the latest log of the supplied evtx file.
    /// If there is no event record, returns an empty string.
    /// </summary>
    /// <param name="filePath"></param>
    /// <returns></returns>
    public static string GetComputerNameFromEvtx(string filePath)
    {
        string computerName = "";
        try
        {
            var elQuery = new EventLogQuery(filePath, PathType.FilePath, query: "*")
            {
                ReverseDirection = true
            };
            var reader = new EventLogReader(elQuery);
            var eventRecord = reader.ReadEvent();
            if (eventRecord != null)
            {
                computerName = eventRecord.MachineName;
            }
        }
        catch (Exception) { }

        return computerName;
    }

    public static bool IsValidEventLog(string path, PathType type)
    {
        if (type == PathType.LogName)
        {
            try
            {
                var ei = new EventLogConfiguration(path);
            }
            catch (Exception)
            {
                return false;
            }
            return true;
        }
        else // PathType.FilePath
        {
            var session = new EventLogSession();
            try
            {
                EventLogInformation logInformation = session.GetLogInformation(path, PathType.FilePath);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
