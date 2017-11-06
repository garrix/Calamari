﻿using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using Calamari.Constants;
using Calamari.Integration.FileSystem;
using Calamari.Integration.Packages.Java;
using Calamari.Util;
using Octopus.Core.Constants;
using Octopus.Core.Extensions;
using Octopus.Core.Resources;
using Octopus.Core.Resources.Metadata;
using Octopus.Core.Resources.Parsing.Maven;
using Octopus.Core.Resources.Versioning;
using Octopus.Core.Resources.Versioning.Factories;
using JavaConstants = Octopus.Core.Constants.JavaConstants;

namespace Calamari.Integration.Packages.Download
{
    public class MavenPackageDownloader : IPackageDownloader
    {
        static readonly IPackageDownloaderUtils PackageDownloaderUtils = new PackageDownloaderUtils();
        static readonly IMavenURLParser MavenUrlParser = new MavenURLParser();
        static readonly IPackageIDParser PackageIdParser = new MavenPackageIDParser();
        static readonly IVersionFactory VersionFactory = new VersionFactory();
        readonly ICalamariFileSystem fileSystem = CalamariPhysicalFileSystem.GetPhysicalFileSystem();

        public void DownloadPackage(
            string packageId,
            IVersion version,
            string feedId,
            Uri feedUri,
            ICredentials feedCredentials,
            bool forcePackageDownload,
            int maxDownloadAttempts,
            TimeSpan downloadAttemptBackoff,
            out string downloadedTo,
            out string hash,
            out long size)
        {
            Log.Info("Getting cache directory");
            try
            {
                PackageDownloaderUtils.GetPackageRoot(feedId);
            }
            catch (Exception ex)
            {
                Log.Error(ex.ToString());
            }

            var cacheDirectory = PackageDownloaderUtils.GetPackageRoot(feedId);

            downloadedTo = null;
            if (!forcePackageDownload)
            {
                Log.Info("Attempting to get from cache");
                try
                {
                    AttemptToGetPackageFromCache(
                        packageId,
                        version,
                        cacheDirectory,
                        out downloadedTo);
                }
                catch (Exception ex)
                {
                    Log.Info("AttemptToGetPackageFromCache() failed");
                    Log.Info(ex.ToString());
                }
            }

            if (downloadedTo == null)
            {
                Log.Info("Downloading from repo");
                DownloadPackage(
                    packageId,
                    version,
                    feedUri,
                    feedCredentials,
                    cacheDirectory,
                    maxDownloadAttempts,
                    downloadAttemptBackoff,
                    out downloadedTo);
            }
            else
            {
                Log.VerboseFormat("Package was found in cache. No need to download. Using file: '{0}'", downloadedTo);
            }

            size = fileSystem.GetFileSize(downloadedTo);
            hash = downloadedTo
                .Map(path => FunctionalExtensions.Using(
                    () => fileSystem.OpenFile(path, FileAccess.Read),
                    stream => HashCalculator.Hash(stream)));
        }

        private void AttemptToGetPackageFromCache(
            string packageId,
            IVersion version,
            string cacheDirectory,
            out string downloadedTo)
        {
            Guard.NotNullOrWhiteSpace(packageId, "packageId can not be null");
            Guard.NotNull(version, "version can not be null");
            Guard.NotNullOrWhiteSpace(cacheDirectory, "cacheDirectory can not be null");
            
            try
            {
                Log.VerboseFormat("Checking package cache for package {0} {1}", packageId, version.ToString());

                fileSystem.EnsureDirectoryExists(cacheDirectory);


                downloadedTo = new MavenPackageID(packageId).FileSystemName
                    .ToEnumerable()
                    // Convert the filename to a search pattern
                    .SelectMany(filename => JarExtractor.EXTENSIONS.Select(extension => filename + "*" + extension))
                    // Convert the search pattern to matching file paths
                    .SelectMany(searchPattern => fileSystem.EnumerateFilesRecursively(cacheDirectory, searchPattern))
                    // Try and extract the package metadata from the file path
                    .Select(file => new Tuple<string, Tuple<bool, PackageMetadata>>(file,
                        PackageIdParser.CanGetMetadataFromServerPackageName(file,
                            new string[] {Path.GetExtension(file)})))
                    // Only keep results where the parsing was successful
                    .Where(fileAndParseResult => fileAndParseResult.Item2.Item1)
                    // Keep the filename and the package metadata
                    .Select(fileAndParseResult =>
                        new Tuple<string, PackageMetadata>(fileAndParseResult.Item1, fileAndParseResult.Item2.Item2))
                    // Only keep results that match the package id and version
                    .Where(fileAndMetadata => fileAndMetadata.Item2.PackageId == packageId)
                    .Where(fileAndMetadata => VersionFactory.CanCreateVersion(fileAndMetadata.Item2.Version.ToString(),
                                                  out IVersion packageVersion, fileAndMetadata.Item2.FeedType) &&
                                              version.Equals(packageVersion))
                    // We only need the filename
                    .Select(fileAndMetadata => fileAndMetadata.Item1)
                    // Get the filename or null
                    .FirstOrDefault();
            }
            catch (Exception ex)
            {
                Log.Info("Failed to scan cache for package");
                Log.Info(ex.ToString());
                throw ex;
            }
        }

        private void DownloadPackage(
            string packageId,
            IVersion version,
            Uri feedUri,
            ICredentials feedCredentials,
            string cacheDirectory,
            int maxDownloadAttempts,
            TimeSpan downloadAttemptBackoff,
            out string downloadedTo)
        {
            Guard.NotNullOrWhiteSpace(packageId, "packageId can not be null");
            Guard.NotNull(version, "version can not be null");
            Guard.NotNullOrWhiteSpace(cacheDirectory, "cacheDirectory can not be null");
            Guard.NotNull(feedUri, "feedUri can not be null");
            
            Log.Info("Downloading Maven package {0} {1} from feed: '{2}'", packageId, version, feedUri);
            Log.VerboseFormat("Downloaded package will be stored in: '{0}'", cacheDirectory);
            fileSystem.EnsureDirectoryExists(cacheDirectory);
            fileSystem.EnsureDiskHasEnoughFreeSpace(cacheDirectory);

            downloadedTo = new MavenPackageID(packageId, version)
                .Map(mavenPackageId => FirstToRespond(mavenPackageId, feedUri))
                .Tee(mavenGavFirst => Log.VerboseFormat("Found package {0} version {1}", packageId, version))
                .Map(mavenGavFirst => DownloadArtifact(
                    mavenGavFirst,
                    packageId,
                    version,
                    feedUri,
                    feedCredentials,
                    cacheDirectory,
                    maxDownloadAttempts,
                    downloadAttemptBackoff));
        }

        string DownloadArtifact(
            MavenPackageID mavenGavFirst,
            string packageId,
            IVersion version,
            Uri feedUri,
            ICredentials feedCredentials,
            string cacheDirectory,
            int maxDownloadAttempts,
            TimeSpan downloadAttemptBackoff)
        {
            Guard.NotNull(mavenGavFirst, "mavenGavFirst can not be null");
            Guard.NotNullOrWhiteSpace(packageId, "packageId can not be null");
            Guard.NotNull(version, "version can not be null");
            Guard.NotNullOrWhiteSpace(cacheDirectory, "cacheDirectory can not be null");
            Guard.NotNull(feedUri, "feedUri can not be null");
            
            return GetFilePathToDownloadPackageTo(
                    cacheDirectory,
                    packageId,
                    version.ToString(),
                    mavenGavFirst.Packaging)
                .Tee(path => FunctionalExtensions.Using(
                    () => fileSystem.OpenFile(path, FileAccess.Write),
                    myStream =>
                    {
                        try
                        {
                            return MavenUrlParser.SanitiseFeedUri(feedUri).ToString().TrimEnd('/')
                                .Map(uri => uri + mavenGavFirst.ArtifactPath)
                                .Map(uri => new HttpClient(new HttpClientHandler {Credentials = feedCredentials})
                                    .GetAsync(uri).Result)
                                .Map(result => result.Content.ReadAsByteArrayAsync().Result)
                                .Tee(content => myStream.Write(content, 0, content.Length));
                        }
                        catch (Exception ex)
                        {
                            Log.Error("Failed to download artifact " + mavenGavFirst.ToString());
                            Log.Error(ex.ToString());
                            throw ex;
                        }
                    }
                ));
        }

        MavenPackageID FirstToRespond(MavenPackageID mavenPackageId, Uri feedUri)
        {
            Guard.NotNull(mavenPackageId, "mavenPackageId can not be null");
            Guard.NotNull(feedUri, "feedUri can not be null");

            return JarExtractor.EXTENSIONS.AsParallel()
                .Select(extension => new MavenPackageID(
                    mavenPackageId.Group,
                    mavenPackageId.Artifact,
                    mavenPackageId.Version,
                    Regex.Replace(extension, "^\\.", "")))
                .FirstOrDefault(mavenGavParser =>
                {
                    try
                    {
                        return MavenUrlParser.SanitiseFeedUri(feedUri).ToString().TrimEnd('/')
                            .Map(uri => uri + mavenGavParser.ArtifactPath)
                            .Map(uri => new HttpRequestMessage(HttpMethod.Head, uri))
                            .Map(request => new HttpClient().SendAsync(request).Result)
                            .Map(result => result.IsSuccessStatusCode);
                    }
                    catch
                    {
                        return false;
                    }
                }) ?? throw new Exception("Failed to find the maven artifact");
        }

        string GetFilePathToDownloadPackageTo(string cacheDirectory, string packageId, string version, string extension)
        {
            Guard.NotNullOrWhiteSpace(cacheDirectory, "cacheDirectory can not be null");
            Guard.NotNullOrWhiteSpace(packageId, "packageId can not be null");
            Guard.NotNullOrWhiteSpace(version, "version can not be null");
            Guard.NotNullOrWhiteSpace(extension, "extension can not be null");
            
            return (packageId + JavaConstants.MavenFilenameDelimiter + version +
                    ServerConstants.SERVER_CACHE_DELIMITER +
                    BitConverter.ToString(Guid.NewGuid().ToByteArray()).Replace("-", string.Empty) +
                    "." + extension)
                .Map(package => Path.Combine(cacheDirectory, package));
        }
    }
}