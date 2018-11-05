﻿using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;

namespace Buildalyzer.Environment
{
    internal class ProcessRunner : IDisposable
    {
        private readonly ILogger<ProcessRunner> _logger;
        private readonly List<string> _output;

        public Process Process { get; }

        public Action Exited { get; set; }

        public ProcessRunner(
            string fileName,
            string arguments,
            string workingDirectory,
            Dictionary<string, string> environmentVariables,
            ILoggerFactory loggerFactory,
            List<string> output = null)
        {
            _logger = loggerFactory?.CreateLogger<ProcessRunner>();
            _output = output;
            Process = new Process();

            // Create the process info
            Process.StartInfo.FileName = fileName;
            Process.StartInfo.Arguments = arguments;
            Process.StartInfo.WorkingDirectory = workingDirectory;
            Process.StartInfo.CreateNoWindow = true;
            Process.StartInfo.UseShellExecute = false;

            // Copy over environment variables
            if(environmentVariables != null)
            {
                foreach(KeyValuePair<string, string> variable in environmentVariables)
                {
                    Process.StartInfo.Environment[variable.Key] = variable.Value;
                    Process.StartInfo.EnvironmentVariables[variable.Key] = variable.Value;
                }
            }

            // Capture output
            if (_logger != null || output != null)
            {
                Process.StartInfo.RedirectStandardOutput = true;
                Process.OutputDataReceived += DataReceived;
            }

            Process.EnableRaisingEvents = true;  // Raises Process.Exited immediatly instead of when checked via .WaitForExit() or .HasExited
            Process.Exited += ProcessExited;
        }
        
        public ProcessRunner Start()
        {
            Process.Start();
            _logger?.LogDebug($"{System.Environment.NewLine}Started process {Process.Id}: \"{Process.StartInfo.FileName}\" {Process.StartInfo.Arguments}{System.Environment.NewLine}");
            if (_logger != null || _output != null)
            {
                Process.BeginOutputReadLine();
            }
            return this;
        }

        private void ProcessExited(object sender, EventArgs e)
        {
            Exited?.Invoke();
            _logger?.LogDebug($"Process {Process.Id} exited with code {Process.ExitCode}{System.Environment.NewLine}{System.Environment.NewLine}");
        }

        public void Dispose()
        {
            if(Process.HasExited)
            {
                // Flush asynchronous output buffer
                // see "Remarks" section in https://docs.microsoft.com/en-us/dotnet/api/system.diagnostics.process.outputdatareceived?view=netframework-4.7.2 
                Process.WaitForExit();
            }
            Process.Exited -= ProcessExited;
            if (_logger != null || _output != null)
            {
                if(!Process.HasExited)
                {
                    // Flush asynchronous output buffer
                    Process.CancelOutputRead();
                    Process.WaitForExit(1000);
                }
                Process.OutputDataReceived -= DataReceived;
            }
            Process.Close();
        }        

        private void DataReceived(object sender, DataReceivedEventArgs e)
        {
            _output?.Add(e.Data);
            _logger?.LogDebug($"{e.Data}{System.Environment.NewLine}");
        }
    }
}
