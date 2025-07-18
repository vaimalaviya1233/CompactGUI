﻿Imports System.Collections.ObjectModel
Imports System.Threading

Imports CompactGUI.Logging.Watcher

Imports Microsoft.Extensions.Logging

Imports Microsoft.Extensions.Logging.Abstractions

Public Class BackgroundCompactor

    Public Property IsCompactorActive As Boolean = False

    Private cancellationTokenSource As New CancellationTokenSource()
    Private isCompacting As Boolean = False
    Private isCompactingPaused As Boolean = False ' Track if compacting is paused

    Private isSystemIdle As Boolean = False

    Private _compactor As Core.Compactor

    Private _excludedFileTypes As String()


    Private Const LAST_SYSTEM_MODIFIED_TIME_THRESHOLD As Integer = 300 ' 5 minutes

    Private _logger As ILogger(Of Watcher)

    Public Sub New(excludedFileTypes As String(), logger As ILogger(Of Watcher))

        _excludedFileTypes = excludedFileTypes
        _logger = logger
        AddHandler IdleDetector.IsIdle, AddressOf OnSystemIdle
        AddHandler IdleDetector.IsNotIdle, AddressOf OnSystemNotIdle

    End Sub

    Private Sub OnSystemIdle(sender As Object, e As EventArgs)
        If Not isSystemIdle Then WatcherLog.SystemIdleDetected(_logger)
        isSystemIdle = True
        ' Attempt to resume only if compacting was paused due to system activity
        If isCompactingPaused AndAlso Not isCompacting Then
            ResumeCompacting()
        End If
    End Sub

    Private Sub OnSystemNotIdle(sender As Object, e As EventArgs)
        If isSystemIdle Then WatcherLog.SystemNotIdle(_logger)
        isSystemIdle = False
        ' Attempt to pause only if compacting is currently active and not already paused
        If isCompacting AndAlso Not isCompactingPaused Then
            PauseCompacting()
        End If
    End Sub

    Public Function BeginCompacting(folder As String, compressionLevel As Core.WOFCompressionAlgorithm) As Task(Of Boolean)

        If compressionLevel = Core.WOFCompressionAlgorithm.NO_COMPRESSION Then Return Task.FromResult(False)

        _compactor = New Core.Compactor(folder, compressionLevel, _excludedFileTypes)

        Return _compactor.RunAsync(Nothing)

    End Function

    Public Async Function StartCompactingAsync(folders As ObservableCollection(Of WatchedFolder), monitors As List(Of FolderMonitor)) As Task(Of Boolean)
        WatcherLog.BackgroundCompactingStarted(_logger)
        Dim cancellationToken As CancellationToken = cancellationTokenSource.Token

        IsCompactorActive = True

        Dim currentProcess As Process = Process.GetCurrentProcess()
        currentProcess.PriorityClass = ProcessPriorityClass.Idle

        Dim foldersCopy As List(Of WatchedFolder) = folders.Where(Function(f) f.DecayPercentage <> 0 AndAlso f.CompressionLevel <> Core.WOFCompressionAlgorithm.NO_COMPRESSION).ToList()

        Dim monitorsCopy As List(Of FolderMonitor) = monitors.ToList()

        For Each folder In foldersCopy
            folder.IsWorking = True
            Dim recentThresholdDate As DateTime = DateTime.Now.AddSeconds(-LAST_SYSTEM_MODIFIED_TIME_THRESHOLD)
            If folder.LastSystemModifiedDate > recentThresholdDate Then
                WatcherLog.SkippingRecentlyModifiedFolder(_logger, folder.DisplayName)
                Continue For
            End If

            WatcherLog.CompactingFolder(_logger, folder.DisplayName)
            Dim compactingTask = BeginCompacting(folder.Folder, folder.CompressionLevel)
            isCompacting = True

            While Not cancellationToken.IsCancellationRequested AndAlso Not compactingTask.IsCompleted
                Await Task.WhenAny(compactingTask, Task.Delay(1000, cancellationToken))

                ' Check the idle state and adjust compacting status accordingly
                If Not isSystemIdle AndAlso Not isCompactingPaused Then
                    PauseCompacting()
                ElseIf isSystemIdle AndAlso isCompactingPaused Then
                    ResumeCompacting()
                End If
            End While

            Dim result = Await compactingTask
            If result AndAlso folders.Contains(folder) Then
                ' Ensure the folder is still in the original collection before updating

                Dim analyser As New Core.Analyser(folder.Folder, NullLogger(Of Core.Analyser).Instance)

                Await analyser.AnalyseFolder(Nothing)

                folder.LastCheckedDate = DateTime.Now
                folder.LastCheckedSize = analyser.CompressedBytes
                folder.LastCompressedSize = analyser.CompressedBytes
                folder.LastSystemModifiedDate = DateTime.Now
                Dim mainCompressionLVL = analyser.FileCompressionDetailsList.Select(Function(f) f.CompressionMode).Max
                folder.CompressionLevel = mainCompressionLVL

                folder.LastCompressedDate = DateTime.Now

                Dim monitor = monitorsCopy.Find(Function(m) m.Folder = folder.Folder)
                If monitor IsNot Nothing AndAlso monitors.Contains(monitor) Then
                    monitor.HasTargetChanged = False
                End If


            End If
            folder.IsWorking = False
            _compactor.Dispose()
            WatcherLog.FinishedCompactingFolder(_logger, folder.DisplayName)
        Next

        IsCompactorActive = False
        isCompacting = False ' Ensure compacting status is reset after operation
        WatcherLog.BackgroundCompactingFinished(_logger)
        currentProcess.PriorityClass = ProcessPriorityClass.Normal
        Return True
    End Function

    Public Sub PauseCompacting()
        WatcherLog.PausingBackgroundCompactor(_logger)
        isCompactingPaused = True ' Indicate compacting is paused
        _compactor.Pause()
    End Sub

    Public Sub ResumeCompacting()
        WatcherLog.ResumingBackgroundCompactor(_logger)
        isCompactingPaused = False ' Indicate compacting is no longer paused
        _compactor.Resume()
    End Sub

End Class
