using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using TagLib;

namespace MediaFileAnalyzer
{
    public class FileOrganizer
    {
        private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".mp3", ".flac", ".m4a", ".wav", ".ogg", ".aac", ".wma"
        };

        public class OrganizationResult
        {
            public int Moved { get; set; }
            public int Skipped { get; set; }
            public int Errors { get; set; }
            public List<string> Moves { get; set; } = new();
            public List<string> Errors_Details { get; set; } = new();
        }

        /// <summary>
        /// Organizes audio files in a directory into Album subdirectories based on metadata tags.
        /// </summary>
        public static async Task<OrganizationResult> OrganizeFilesAsync(string rootPath, bool dryRun = false, IProgress<(int Current, int Total, string FileName)>? progress = null)
        {
            var result = new OrganizationResult();
            var movedSourceDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (!Directory.Exists(rootPath))
            {
                result.Errors_Details.Add($"Directory not found: {rootPath}");
                result.Errors++;
                return result;
            }

            var audioFiles = Directory.EnumerateFiles(rootPath, "*.*", SearchOption.AllDirectories)
                .Where(f => AudioExtensions.Contains(Path.GetExtension(f)))
                .ToList();

            int processedCount = 0;
            int totalCount = audioFiles.Count;

            foreach (var sourceFile in audioFiles)
            {
                try
                {
                    var (_, album) = ExtractMetadata(sourceFile);
                    var sanitizedAlbum = SanitizeName(album);

                    // Organize all tracks by Album only.
                    var destDir = Path.Combine(rootPath, sanitizedAlbum);
                    var destFile = Path.Combine(destDir, Path.GetFileName(sourceFile));

                    // Check if file is already in the correct location
                    if (Path.GetFullPath(sourceFile) == Path.GetFullPath(destFile))
                    {
                        result.Skipped++;
                        result.Moves.Add($"SKIP (same): {sourceFile}");
                        processedCount++;
                        progress?.Report((processedCount, totalCount, Path.GetFileName(sourceFile)));
                        continue;
                    }

                    if (!dryRun)
                    {
                        // Create destination directory if it doesn't exist
                        Directory.CreateDirectory(destDir);

                        // Handle filename collision
                        if (System.IO.File.Exists(destFile))
                        {
                            destFile = GetUniqueFilePath(destFile);
                        }

                        // Move the file
                        System.IO.File.Move(sourceFile, destFile, overwrite: false);

                        var sourceDirectory = Path.GetDirectoryName(sourceFile);
                        if (!string.IsNullOrWhiteSpace(sourceDirectory))
                        {
                            movedSourceDirectories.Add(sourceDirectory);
                        }
                    }

                    result.Moved++;
                    result.Moves.Add($"MOVE: {sourceFile} -> {destFile}");
                    processedCount++;
                    progress?.Report((processedCount, totalCount, Path.GetFileName(sourceFile)));
                }
                catch (Exception ex)
                {
                    result.Errors++;
                    result.Errors_Details.Add($"ERROR: {sourceFile} : {ex.Message}");
                    result.Moves.Add($"ERROR: {sourceFile} : {ex.Message}");
                    processedCount++;
                    progress?.Report((processedCount, totalCount, Path.GetFileName(sourceFile)));
                }
            }

            if (!dryRun && movedSourceDirectories.Count > 0)
            {
                CleanupMovedSourceDirectories(movedSourceDirectories, rootPath, result);
            }

            return result;
        }

        private static void CleanupMovedSourceDirectories(
            IEnumerable<string> movedSourceDirectories,
            string rootPath,
            OrganizationResult result)
        {
            var stopAt = NormalizeDirectoryPath(rootPath);

            foreach (var startDirectory in movedSourceDirectories
                .Select(NormalizeDirectoryPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(path => path.Length))
            {
                try
                {
                    CleanupEmptyDirectories(startDirectory, stopAt);
                }
                catch (Exception ex)
                {
                    result.Errors++;
                    result.Errors_Details.Add($"CLEANUP ERROR: {startDirectory} : {ex.Message}");
                }
            }
        }

        private static void CleanupEmptyDirectories(string startDirectory, string stopAtDirectory)
        {
            string current = NormalizeDirectoryPath(startDirectory);
            string stopAt = NormalizeDirectoryPath(stopAtDirectory);

            while (IsPathWithinRoot(current, stopAt) &&
                   !string.Equals(current, stopAt, StringComparison.OrdinalIgnoreCase))
            {
                if (!Directory.Exists(current))
                {
                    break;
                }

                if (Directory.EnumerateFileSystemEntries(current).Any())
                {
                    break;
                }

                Directory.Delete(current, recursive: false);

                var parent = Directory.GetParent(current);
                if (parent == null)
                {
                    break;
                }

                current = NormalizeDirectoryPath(parent.FullName);
            }
        }

        private static string NormalizeDirectoryPath(string path)
        {
            var fullPath = Path.GetFullPath(path);
            var normalized = fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            if (!string.IsNullOrEmpty(normalized))
            {
                return normalized;
            }

            return Path.GetPathRoot(fullPath) ?? fullPath;
        }

        private static bool IsPathWithinRoot(string candidatePath, string rootPath)
        {
            if (string.Equals(candidatePath, rootPath, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            var rootedPrefix = rootPath + Path.DirectorySeparatorChar;
            var alternatePrefix = rootPath + Path.AltDirectorySeparatorChar;

            return candidatePath.StartsWith(rootedPrefix, StringComparison.OrdinalIgnoreCase) ||
                   candidatePath.StartsWith(alternatePrefix, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Extracts Artist and Album metadata from an audio file.
        /// </summary>
        private static (string Artist, string Album) ExtractMetadata(string filePath)
        {
            try
            {
                var file = TagLib.File.Create(filePath);
                var tag = file.Tag;

                // Prefer AlbumArtist, fall back to Artist
                string artist = !string.IsNullOrEmpty(tag.FirstAlbumArtist) ? tag.FirstAlbumArtist
                              : !string.IsNullOrEmpty(tag.FirstPerformer) ? tag.FirstPerformer
                              : "Unknown Artist";
                string album = !string.IsNullOrEmpty(tag.Album) ? tag.Album : "Unknown Album";

                return (artist, album);
            }
            catch
            {
                return ("Unknown Artist", "Unknown Album");
            }
        }

        /// <summary>
        /// Sanitizes a string to be safe for use as a directory name.
        /// Removes or replaces characters that are invalid on most filesystems.
        /// </summary>
        public static string SanitizeName(string name)
        {
            if (string.IsNullOrEmpty(name))
                return "Unknown";

            name = name.Trim();

            // Replace problematic characters with underscores
            var invalidChars = Regex.Escape(new string(Path.GetInvalidFileNameChars()) + new string(Path.GetInvalidPathChars()));
            var pattern = $"[{invalidChars}:?*<>|\"]";
            name = Regex.Replace(name, pattern, "_");

            // Collapse multiple spaces
            name = Regex.Replace(name, @"\s+", " ");

            return name;
        }

        /// <summary>
        /// Generates a unique file path by appending a number in parentheses if the file already exists.
        /// </summary>
        private static string GetUniqueFilePath(string filePath)
        {
            if (!System.IO.File.Exists(filePath))
                return filePath;

            var directory = Path.GetDirectoryName(filePath);
            var fileName = Path.GetFileNameWithoutExtension(filePath);
            var extension = Path.GetExtension(filePath);

            int counter = 1;
            string newPath;
            do
            {
                newPath = Path.Combine(directory ?? "", $"{fileName} ({counter}){extension}");
                counter++;
            } while (System.IO.File.Exists(newPath));

            return newPath;
        }
    }
}
