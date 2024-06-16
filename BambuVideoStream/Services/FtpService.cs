using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Xml.Linq;
using System.Xml.XPath;
using BambuVideoStream.Models;
using BambuVideoStream.Utilities;
using FluentFTP;
using FluentFTP.Exceptions;
using FluentFTP.GnuTLS;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;


namespace BambuVideoStream;

public class FtpService(
    IOptions<BambuSettings> options,
    ILogger<FtpService> logger,
    IHostApplicationLifetime lifetime)
{
    private readonly ILogger<FtpService> log = logger;
    private readonly BambuSettings settings = options.Value;
    private readonly CancellationToken ct = lifetime.ApplicationStopping;

    /// <exception cref="ObjectDisposedException"></exception>
    public IList<FtpListItem> ListDirectory(string dir)
    {
        try
        {
            this.ct.ThrowIfCancellationRequested();

            using var ftp = this.GetFtpClient();
            var directory = ftp.Value.GetListing(dir);
            return directory;
        }
        catch (OperationCanceledException e)
        {
            throw new ObjectDisposedException($"{nameof(FtpService)} is disposed", e);
        }
    }

    /// <exception cref="ObjectDisposedException"></exception>
    public bool FileExists(string filename)
    {
        try
        {
            this.ct.ThrowIfCancellationRequested();

            using var ftp = this.GetFtpClient();
            return ftp.Value.FileExists(filename);
        }
        catch (OperationCanceledException e)
        {
            throw new ObjectDisposedException($"{nameof(FtpService)} is disposed", e);
        }
    }

    /// <exception cref="FtpMissingObjectException"></exception>
    /// <exception cref="ObjectDisposedException"></exception>
    public byte[] GetFileThumbnail(string filename)
    {
        try
        {
            this.ct.ThrowIfCancellationRequested();

            using var file = new MemoryStream();

            using (var ftp = this.GetFtpClient())
            {
                if (!ftp.Value.DownloadStream(file, filename)) // Could also throw FtpMissingObjectException
                {
                    throw new FtpMissingObjectException($"File {filename} not found", new FileNotFoundException(), filename, FtpObjectType.File);
                }
            }
            
            file.Position = 0;
            this.ct.ThrowIfCancellationRequested();
            using var archive = new ZipArchive(file, ZipArchiveMode.Read);

            string previewFileName = "Metadata/plate_1.png";
            if (filename.Contains("plate_2"))
            {
                previewFileName = "Metadata/plate_2.png";
            }

            this.ct.ThrowIfCancellationRequested();
            using var entryStream = archive.GetEntry(previewFileName).Open();
            this.ct.ThrowIfCancellationRequested();

            using var outputStream = new MemoryStream();
            entryStream.CopyTo(outputStream);
            return outputStream.ToArray();
        }
        catch (FtpMissingObjectException e)
        {
            this.log.LogWarning(e, "Couldn't find file '{filename}'", filename);
            return null;
        }
        catch (OperationCanceledException e)
        {
            throw new ObjectDisposedException($"{nameof(FtpService)} is disposed", e);
        }
    }

    /// <exception cref="ObjectDisposedException"></exception>
    /// <remarks>Swallows (but logs) runtime exceptions other than <see cref="ObjectDisposedException"/></remarks>
    public string GetPrintJobWeight(string filename)
    {
        try
        {
            this.ct.ThrowIfCancellationRequested();

            using var file = new MemoryStream();

            using (var ftp = this.GetFtpClient())
            {
                if (!ftp.Value.DownloadStream(file, filename))
                {
                    throw new FileNotFoundException($"File '{filename}' not found");
                }
            }

            file.Position = 0;
            this.ct.ThrowIfCancellationRequested();
            using var archive = new ZipArchive(file, ZipArchiveMode.Read);

            string configFileName = "Metadata/slice_info.config";

            this.ct.ThrowIfCancellationRequested();
            using var reader = new StreamReader(archive.GetEntry(configFileName).Open());
            this.ct.ThrowIfCancellationRequested();
            string xml = reader.ReadToEnd();

            var doc = XDocument.Parse(xml);
            var filamentNode = doc.XPathSelectElement("//filament");
            var weight = filamentNode.Attribute("used_g").Value;
            return weight;
        }
        catch (FtpMissingObjectException)
        {
            this.log.LogWarning("Couldn't find file '{filename}'", filename);
        }
        catch (OperationCanceledException e)
        {
            throw new ObjectDisposedException($"{nameof(FtpService)} is disposed", e);
        }
        catch (Exception ex)
        {
            this.log.LogError(ex, "Error getting print job weight");
        }

        return null;
    }

    private DisposableObjectHolder<FtpClient> GetFtpClient()
    {
        var ftp = new FtpClient(
            this.settings.IpAddress,
            this.settings.Username,
            this.settings.Password,
            this.settings.FtpPort,
            new FtpConfig
            {
                LogHost = true,
                ValidateAnyCertificate = true,
                EncryptionMode = FtpEncryptionMode.Implicit,
                CustomStream = typeof(GnuTlsStream),
                DataConnectionType = FtpDataConnectionType.EPSV,
                DownloadDataType = FtpDataType.Binary
            },
            new FtpLogger(this.log));
        ftp.Connect();
        var holder = new DisposableObjectHolder<FtpClient>(ftp);
        var reg = this.ct.Register(ftp.Disconnect);
        holder.Disposing += (_, _) => reg.Dispose();
        return holder;
    }

    private class FtpLogger(ILogger<FtpService> logger) : IFtpLogger
    {
        private readonly ILogger<FtpService> log = logger;

        public void Log(FtpLogEntry entry)
        {
            var level = entry.Severity switch
            {
                FtpTraceLevel.Error => LogLevel.Error,
                FtpTraceLevel.Warn => LogLevel.Warning,
                _ => LogLevel.Trace
            };
            log.Log(level, entry.Exception, "{message}", entry.Message);
        }
    }
}
