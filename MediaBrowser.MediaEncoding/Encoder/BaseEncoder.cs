using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Diagnostics;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging;

namespace MediaBrowser.MediaEncoding.Encoder
{
    public abstract class BaseEncoder
    {
        protected readonly MediaEncoder MediaEncoder;
        protected readonly ILogger Logger;
        protected readonly IServerConfigurationManager ConfigurationManager;
        protected readonly IFileSystem FileSystem;
        protected readonly IIsoManager IsoManager;
        protected readonly ILibraryManager LibraryManager;
        protected readonly ISessionManager SessionManager;
        protected readonly ISubtitleEncoder SubtitleEncoder;
        protected readonly IMediaSourceManager MediaSourceManager;
        protected IProcessFactory ProcessFactory;

        protected EncodingHelper EncodingHelper;

        protected BaseEncoder(
            MediaEncoder mediaEncoder,
            ILogger logger,
            IServerConfigurationManager configurationManager,
            IFileSystem fileSystem,
            IIsoManager isoManager,
            ILibraryManager libraryManager,
            ISessionManager sessionManager,
            ISubtitleEncoder subtitleEncoder,
            IMediaSourceManager mediaSourceManager,
            IProcessFactory processFactory)
        {
            MediaEncoder = mediaEncoder;
            Logger = logger;
            ConfigurationManager = configurationManager;
            FileSystem = fileSystem;
            IsoManager = isoManager;
            LibraryManager = libraryManager;
            SessionManager = sessionManager;
            SubtitleEncoder = subtitleEncoder;
            MediaSourceManager = mediaSourceManager;
            ProcessFactory = processFactory;

            EncodingHelper = new EncodingHelper(MediaEncoder, FileSystem, SubtitleEncoder);
        }

        public async Task<EncodingJob> Start(
            EncodingJobOptions options,
            IProgress<double> progress,
            CancellationToken cancellationToken)
        {
            var encodingJob = await new EncodingJobFactory(Logger, LibraryManager, MediaSourceManager, MediaEncoder)
                .CreateJob(options, EncodingHelper, IsVideoEncoder, progress, cancellationToken).ConfigureAwait(false);

            encodingJob.OutputFilePath = GetOutputFilePath(encodingJob);
            Directory.CreateDirectory(Path.GetDirectoryName(encodingJob.OutputFilePath));

            encodingJob.ReadInputAtNativeFramerate = options.ReadInputAtNativeFramerate;

            await AcquireResources(encodingJob, cancellationToken).ConfigureAwait(false);

            var commandLineArgs = GetCommandLineArguments(encodingJob);

            Process process = Process.Start(new ProcessStartInfo
            {
                WindowStyle = ProcessWindowStyle.Hidden,
                CreateNoWindow = true,
                UseShellExecute = false,

                // Must consume both stdout and stderr or deadlocks may occur
                //RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,

                FileName = MediaEncoder.EncoderPath,
                Arguments = commandLineArgs,

                ErrorDialog = false
            });

            process.EnableRaisingEvents = true;

            var workingDirectory = GetWorkingDirectory(options);
            if (!string.IsNullOrWhiteSpace(workingDirectory))
            {
                process.StartInfo.WorkingDirectory = workingDirectory;
            }

            OnTranscodeBeginning(encodingJob);

            var commandLineLogMessage = process.StartInfo.FileName + " " + process.StartInfo.Arguments;
            Logger.LogInformation(commandLineLogMessage);

            var logFilePath = Path.Combine(ConfigurationManager.CommonApplicationPaths.LogDirectoryPath, "transcode-" + Guid.NewGuid() + ".txt");
            Directory.CreateDirectory(Path.GetDirectoryName(logFilePath));

            // FFMpeg writes debug/error info to stderr. This is useful when debugging so let's put it in the log directory.
            encodingJob.LogFileStream = FileSystem.GetFileStream(logFilePath, FileOpenMode.Create, FileAccessMode.Write, FileShareMode.Read, true);

            var commandLineLogMessageBytes = Encoding.UTF8.GetBytes(commandLineLogMessage + Environment.NewLine + Environment.NewLine);
            await encodingJob.LogFileStream.WriteAsync(commandLineLogMessageBytes, 0, commandLineLogMessageBytes.Length, cancellationToken).ConfigureAwait(false);

            process.Exited += (sender, args) => OnFfMpegProcessExited(process, encodingJob);

            try
            {
                process.Start();
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error starting ffmpeg");

                OnTranscodeFailedToStart(encodingJob.OutputFilePath, encodingJob);

                throw;
            }

            cancellationToken.Register(async () => await Cancel(process, encodingJob));

            // MUST read both stdout and stderr asynchronously or a deadlock may occur
            //process.BeginOutputReadLine();

            // Important - don't await the log task or we won't be able to kill ffmpeg when the user stops playback
            new JobLogger(Logger).StartStreamingLog(encodingJob, process.StandardError.BaseStream, encodingJob.LogFileStream);

            // Wait for the file to or for the process to stop
            Task file = WaitForFileAsync(encodingJob.OutputFilePath);
            await Task.WhenAny(encodingJob.TaskCompletionSource.Task, file).ConfigureAwait(false);

            return encodingJob;
        }

        public static Task WaitForFileAsync(string path)
        {
            if (File.Exists(path))
            {
                return Task.CompletedTask;
            }

            var tcs = new TaskCompletionSource<bool>();
            FileSystemWatcher watcher = new FileSystemWatcher(Path.GetDirectoryName(path));

            watcher.Created += (s, e) =>
            {
                if (e.Name == Path.GetFileName(path))
                {
                    watcher.Dispose();
                    tcs.TrySetResult(true);
                }
            };

            watcher.Renamed += (s, e) =>
            {
                if (e.Name == Path.GetFileName(path))
                {
                    watcher.Dispose();
                    tcs.TrySetResult(true);
                }
            };

            watcher.EnableRaisingEvents = true;

            return tcs.Task;
        }

        private async Task Cancel(Process process, EncodingJob job)
        {
            Logger.LogInformation("Killing ffmpeg process for {0}", job.OutputFilePath);

            //process.Kill();
            await process.StandardInput.WriteLineAsync("q");

            job.IsCancelled = true;
        }

        /// <summary>
        /// Processes the exited.
        /// </summary>
        /// <param name="process">The process.</param>
        /// <param name="job">The job.</param>
        private void OnFfMpegProcessExited(Process process, EncodingJob job)
        {
            job.HasExited = true;

            Logger.LogDebug("Disposing stream resources");
            job.Dispose();

            var isSuccessful = false;

            try
            {
                var exitCode = process.ExitCode;
                Logger.LogInformation("FFMpeg exited with code {0}", exitCode);

                isSuccessful = exitCode == 0;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "FFMpeg exited with an error.");
            }

            if (isSuccessful && !job.IsCancelled)
            {
                job.TaskCompletionSource.TrySetResult(true);
            }
            else if (job.IsCancelled)
            {
                try
                {
                    DeleteFiles(job);
                }
                catch
                {
                }
                try
                {
                    job.TaskCompletionSource.TrySetException(new OperationCanceledException());
                }
                catch
                {
                }
            }
            else
            {
                try
                {
                    DeleteFiles(job);
                }
                catch
                {
                }
                try
                {
                    job.TaskCompletionSource.TrySetException(new Exception("Encoding failed"));
                }
                catch
                {
                }
            }

            // This causes on exited to be called twice:
            //try
            //{
            //    // Dispose the process
            //    process.Dispose();
            //}
            //catch (Exception ex)
            //{
            //    Logger.LogError("Error disposing ffmpeg.", ex);
            //}
        }

        protected virtual void DeleteFiles(EncodingJob job)
        {
            FileSystem.DeleteFile(job.OutputFilePath);
        }

        private void OnTranscodeBeginning(EncodingJob job)
        {
            job.ReportTranscodingProgress(null, null, null, null, null);
        }

        private void OnTranscodeFailedToStart(string path, EncodingJob job)
        {
            if (!string.IsNullOrWhiteSpace(job.Options.DeviceId))
            {
                SessionManager.ClearTranscodingInfo(job.Options.DeviceId);
            }
        }

        protected abstract bool IsVideoEncoder { get; }

        protected virtual string GetWorkingDirectory(EncodingJobOptions options)
        {
            return null;
        }

        protected EncodingOptions GetEncodingOptions()
        {
            return ConfigurationManager.GetConfiguration<EncodingOptions>("encoding");
        }

        protected abstract string GetCommandLineArguments(EncodingJob job);

        private string GetOutputFilePath(EncodingJob state)
        {
            var folder = string.IsNullOrWhiteSpace(state.Options.TempDirectory) ?
                ConfigurationManager.ApplicationPaths.TranscodingTempPath :
                state.Options.TempDirectory;

            var outputFileExtension = GetOutputFileExtension(state);

            var filename = state.Id + (outputFileExtension ?? string.Empty).ToLowerInvariant();
            return Path.Combine(folder, filename);
        }

        protected virtual string GetOutputFileExtension(EncodingJob state)
        {
            if (!string.IsNullOrWhiteSpace(state.Options.Container))
            {
                return "." + state.Options.Container;
            }

            return null;
        }

        /// <summary>
        /// Gets the name of the output video codec
        /// </summary>
        /// <param name="state">The state.</param>
        /// <returns>System.String.</returns>
        protected string GetVideoDecoder(EncodingJob state)
        {
            if (string.Equals(state.OutputVideoCodec, "copy", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            // Only use alternative encoders for video files.
            // When using concat with folder rips, if the mfx session fails to initialize, ffmpeg will be stuck retrying and will not exit gracefully
            // Since transcoding of folder rips is expiremental anyway, it's not worth adding additional variables such as this.
            if (state.VideoType != VideoType.VideoFile)
            {
                return null;
            }

            if (state.VideoStream != null && !string.IsNullOrWhiteSpace(state.VideoStream.Codec))
            {
                if (string.Equals(GetEncodingOptions().HardwareAccelerationType, "qsv", StringComparison.OrdinalIgnoreCase))
                {
                    switch (state.MediaSource.VideoStream.Codec.ToLowerInvariant())
                    {
                        case "avc":
                        case "h264":
                            if (MediaEncoder.SupportsDecoder("h264_qsv"))
                            {
                                // Seeing stalls and failures with decoding. Not worth it compared to encoding.
                                return "-c:v h264_qsv ";
                            }
                            break;
                        case "mpeg2video":
                            if (MediaEncoder.SupportsDecoder("mpeg2_qsv"))
                            {
                                return "-c:v mpeg2_qsv ";
                            }
                            break;
                        case "vc1":
                            if (MediaEncoder.SupportsDecoder("vc1_qsv"))
                            {
                                return "-c:v vc1_qsv ";
                            }
                            break;
                    }
                }
            }

            // leave blank so ffmpeg will decide
            return null;
        }

        private async Task AcquireResources(EncodingJob state, CancellationToken cancellationToken)
        {
            if (state.VideoType == VideoType.Iso && state.IsoType.HasValue && IsoManager.CanMount(state.MediaPath))
            {
                state.IsoMount = await IsoManager.Mount(state.MediaPath, cancellationToken).ConfigureAwait(false);
            }

            if (state.MediaSource.RequiresOpening && string.IsNullOrWhiteSpace(state.Options.LiveStreamId))
            {
                var liveStreamResponse = await MediaSourceManager.OpenLiveStream(new LiveStreamRequest
                {
                    OpenToken = state.MediaSource.OpenToken

                }, cancellationToken).ConfigureAwait(false);

                EncodingHelper.AttachMediaSourceInfo(state, liveStreamResponse.MediaSource, null);

                if (state.IsVideoRequest)
                {
                    EncodingHelper.TryStreamCopy(state);
                }
            }

            if (state.MediaSource.BufferMs.HasValue)
            {
                await Task.Delay(state.MediaSource.BufferMs.Value, cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
