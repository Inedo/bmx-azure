﻿using System;
using System.IO;
using System.Linq;
using System.Threading;
using Inedo.BuildMaster;
using Inedo.BuildMaster.Extensibility.Actions;
using Inedo.BuildMaster.Files;
using Inedo.BuildMaster.Web;
using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Auth;

namespace Inedo.BuildMasterExtensions.Azure.Storage
{
    [ActionProperties(
        "Upload Files to Blob Storage",
        "Uploads files from a BuildMaster Server to Windows Azure Blob Storage.")]
    [Tag("windows-azure")]
    [CustomEditor(typeof(UploadFilesToBlobStorageActionEditor))]
    public sealed class UploadFilesToBlobStorageAction : RemoteActionBase
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="UploadFilesToBlobStorageAction"/> class.
        /// </summary>
        public UploadFilesToBlobStorageAction()
        {
        }

        [Persistent]
        public string AccountName { get; set; }
        [Persistent]
        public string AccessKey { get; set; }
        [Persistent]
        public string Container { get; set; }
        [Persistent]
        public string[] FileMasks { get; set; }
        [Persistent]
        public bool Recursive { get; set; }
        [Persistent]
        public string TargetFolder { get; set; }

        public override string ToString()
        {
            return string.Format(
                "Upload files matching ({0}) from {1} to {2}",
                this.FileMasks != null ? string.Join(", ", this.FileMasks) : string.Empty,
                string.IsNullOrEmpty(this.OverriddenSourceDirectory) ? "(default)" : this.OverriddenSourceDirectory,
                this.Container + Util.ConcatNE("/", this.TargetFolder)
            );
        }

        protected override void Execute()
        {
            this.ExecuteRemoteCommand("upload");
        }
        protected override string ProcessRemoteCommand(string name, string[] args)
        {
            if (name != "upload")
                throw new ArgumentException("Invalid command.");

            var entryResults = Util.Files.GetDirectoryEntry(
                new GetDirectoryEntryCommand
                {
                    Path = this.Context.SourceDirectory,
                    IncludeRootPath = true,
                    Recurse = this.Recursive
                }
            );

            var matches = Util.Files.Comparison.GetMatches(
                this.Context.SourceDirectory,
                entryResults.Entry,
                this.FileMasks
            ).OfType<FileEntryInfo>().ToList();

            if (matches.Count == 0)
            {
                this.LogWarning("No files matched with the specified mask.");
                return null;
            }

            this.LogDebug("Mask matched {0} file(s).", matches.Count);

            this.LogInformation("Contacting Windows Azure Storage Service...");
            var account = new CloudStorageAccount(new StorageCredentials(this.AccountName, this.AccessKey), true);
            var blobClient = account.CreateCloudBlobClient();

            this.LogDebug("Getting container {0}...", this.Container);
            var container = blobClient.GetContainerReference(this.Container);
            if (container.CreateIfNotExists())
                this.LogDebug("Container created.");
            else
                this.LogDebug("Container found.");

            var targetFolder = (this.TargetFolder ?? "").Trim('/');
            if (targetFolder != string.Empty)
                targetFolder += "/";

            this.LogDebug("Uploading to {0}...", targetFolder);
            foreach (var fileInfo in matches)
            {
                var fileName = targetFolder + fileInfo.Path.Substring(this.Context.SourceDirectory.Length).Replace(Path.DirectorySeparatorChar, '/').Trim('/');
                this.LogInformation("Transferring {0} to {1}...", fileInfo.Path, fileName);
                try
                {
                    var blob = container.GetBlockBlobReference(fileName);

                    var transfer = new BlobTransfer(blob);
                    transfer.TransferProgressChanged += (s, e) =>
                    {
                        this.LogDebug("Upload Progress: {0}/{1} ({2}%) - Est. Time Remaining: {3}", e.BytesSent, e.TotalBytesToSend, e.ProgressPercentage, e.TimeRemaining);
                    };
                    transfer.TransferCompleted += (s, e) =>
                    {
                        this.LogDebug("File {0} uploaded successfully.", fileName);
                    };

                    var result = transfer.UploadBlobAsync(fileInfo.Path);

                    int handled = WaitHandle.WaitAny(new[] { result.AsyncWaitHandle, this.Context.CancellationToken.WaitHandle });
                    if (handled == 1)
                    {
                        result.Cancel();
                        this.ThrowIfCanceledOrTimeoutExpired();
                    }
                }
                catch (Exception ex)
                {
                    if (this.ResumeNextOnError)
                        this.LogError("Upload failed: {0}", ex.Message);
                    else
                        throw;
                }
            }

            return null;
        }
    }
}
