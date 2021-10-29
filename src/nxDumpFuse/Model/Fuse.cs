﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using nxDumpFuse.Events;
using nxDumpFuse.Extensions;
using nxDumpFuse.Model.Enums;

namespace nxDumpFuse.Model
{
    public class Fuse
    {
        
        private readonly CancellationTokenSource _cts;
        private readonly string _inputFilePath;
        private readonly string _outputDir;
        private string _outputFilePath = string.Empty;
        private FileCase _fileCase;
        private readonly Stopwatch _sw = new();

        public Fuse(string inputFilePath, string outputDir)
        {
            _inputFilePath = inputFilePath;
            _outputDir = outputDir;
            _cts = new CancellationTokenSource();
        }

        public event EventHandlers.FuseUpdateEventHandler? FuseUpdateEvent;
        public event EventHandlers.FuseSimpleLogEventHandler? FuseSimpleLogEvent;

        protected virtual void OnFuseUpdate(FuseUpdateInfo fuseUpdateInfo)
        {
            FuseUpdateEvent?.Invoke(fuseUpdateInfo);
        }

        private void Update(int part, int parts, double progress, double progressPart, long speed, bool complete = false)
        {
            OnFuseUpdate(new FuseUpdateInfo
            {
                Part = part,
                Parts = parts,
                Progress = progress,
                ProgressPart = progressPart,
                Speed = speed.ToMb(),
                Complete = complete
            });
        }

        protected virtual void OnFuseSimpleLogEvent(FuseSimpleLog log)
        {
            FuseSimpleLogEvent?.Invoke(log);
        }

        private void Log(FuseSimpleLogType type, string message)
        {
            OnFuseSimpleLogEvent(new FuseSimpleLog(type, DateTime.Now, message));
        }

        public void Start()
        {
            if (string.IsNullOrEmpty(_inputFilePath))
            {
                Log(FuseSimpleLogType.Error, "Input File cannot be empty");
                return;
            }
            if (string.IsNullOrEmpty(_outputDir))
            {
                Log(FuseSimpleLogType.Error, "Output Directory cannot be empty");
                return;
            }
            
            (_outputFilePath, _fileCase) = _inputFilePath.GetOutputFilePath(_outputDir);
            if (string.IsNullOrEmpty(_outputFilePath) || _fileCase == FileCase.Invalid)
            {
                Log(FuseSimpleLogType.Error, "Output path was null");
                return;
            }

            var inputFiles = _inputFilePath.GetInputFiles(_fileCase);
            if (inputFiles.Count == 0)
            {
                Log(FuseSimpleLogType.Error, "No input files found");
                return;
            }
            
            FuseFiles(inputFiles);
        }

        public void Stop()
        {
            _cts.Cancel();
            _sw.Stop();

            Log(FuseSimpleLogType.Information, "Fuse Stopped");

            if (File.Exists(_outputFilePath))
            {
                Task.Run((() =>
                {
                    const int retries = 5;
                    for (var i = 0; i <= retries; i++)
                    {
                        try
                        {
                            File.Delete(_outputFilePath);
                            Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => Log(FuseSimpleLogType.Information, $"Deleted {_outputFilePath}"));
                            Update(0, 0, 0, 0, 0);
                            break;
                        }
                        catch (IOException)
                        {
                            Thread.Sleep(1000);
                        }
                    }
                }));
            }
        }

        private async void FuseFiles(List<string> inputFiles)
        {
            var buffer = new byte[1024 * 1024];
            var count = 0;
            long totalBytes = 0;
            var totalFileLength = inputFiles.GetOutputFileSize();

            Log(FuseSimpleLogType.Information, $"Fusing {inputFiles.Count} parts to {_outputFilePath}  ({totalFileLength.ToMb()}MB)");

            _sw.Start();
            await using var outputStream = File.Create(_outputFilePath);
            foreach (var inputFilePath in inputFiles)
            {
                if (_cts.Token.IsCancellationRequested) return;
                long currentBytes = 0;
                int currentBlockSize;
                long copySpeed = 0;

                await using var inputStream = File.OpenRead(inputFilePath);
                var fileLength = inputStream.Length;

                Log(FuseSimpleLogType.Information, $"Fusing file part {++count}-> {inputFilePath} ({fileLength.ToMb()}MB)");

                while ((currentBlockSize = inputStream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    if (_cts.Token.IsCancellationRequested) return;

                    currentBytes += currentBlockSize;
                    totalBytes += currentBlockSize;

                    try
                    {
                        await outputStream.WriteAsync(buffer, 0, currentBlockSize, _cts.Token);
                    }
                    catch (TaskCanceledException e)
                    {
                        Log(FuseSimpleLogType.Error, e.Message);
                        _sw.Stop();
                        Update(0,0,0,0,0,true);
                        return;
                    }

                    var progress = totalBytes * 100.0 / totalFileLength;
                    var progressPart = currentBytes * 100.0 / fileLength;
                    if(_sw.ElapsedMilliseconds >= 1000) copySpeed = totalBytes / _sw.ElapsedMilliseconds.ToSeconds();
                    Update(count, inputFiles.Count, progress, progressPart, copySpeed);
                }
            }
            
            Log(FuseSimpleLogType.Information, $"Fuse Completed in {_sw.ElapsedMilliseconds.ToSeconds()}s");
            _sw.Stop();
            Update(0, 0, 0, 0, 0, true);
        }
    }
}