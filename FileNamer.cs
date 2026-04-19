using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TagLib;
using File = System.IO.File;

namespace MediaFileAnalyzer
{
    public static class FileNamer
    {
        /// <summary>
        /// Scans the provided file paths and returns a set of album names that should be treated
        /// as compilations (i.e., same album with more than one distinct track artist and no
        /// AlbumArtist tag set on any of the files).  The returned set uses
        /// OrdinalIgnoreCase comparison so it can be fed directly into GetPicardPath.
        /// </summary>
        public static HashSet<string> DetectCompilationAlbums(IEnumerable<string> filePaths)
        {
            // album name -> list of (trackArtist, albumArtist)
            var albumGroups = new Dictionary<string, List<(string trackArtist, string albumArtist)>>(StringComparer.OrdinalIgnoreCase);

            foreach (var filePath in filePaths)
            {
                try
                {
                    using var audioFile = TagLib.File.Create(filePath);
                    var tag = audioFile.Tag;
                    string album = !string.IsNullOrWhiteSpace(tag.Album) ? tag.Album : "Singles";
                    string trackArtist = tag.FirstPerformer ?? string.Empty;
                    string albumArtist = tag.FirstAlbumArtist ?? string.Empty;

                    if (!albumGroups.TryGetValue(album, out var list))
                    {
                        list = new List<(string, string)>();
                        albumGroups[album] = list;
                    }
                    list.Add((trackArtist, albumArtist));
                }
                catch
                {
                    // Skip files whose tags cannot be read.
                }
            }

            var compilations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var kvp in albumGroups)
            {
                var entries = kvp.Value;

                // If any file already carries an AlbumArtist the folder name is already correct.
                bool hasAlbumArtist = entries.Any(e => !string.IsNullOrWhiteSpace(e.albumArtist));
                if (hasAlbumArtist)
                    continue;

                // Multiple distinct track artists → compilation.
                int distinctArtists = entries
                    .Select(e => e.trackArtist.Trim())
                    .Where(a => !string.IsNullOrWhiteSpace(a))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count();

                if (distinctArtists > 1)
                    compilations.Add(kvp.Key);
            }

            return compilations;
        }

        /// <summary>
        /// Gets the Picard-style file path based on audio metadata.
        /// Format: AlbumArtist/Album/[DiscNumber-]TrackNumber - Title
        /// Matches Picard convention: $if2(%albumartist%,%artist%) - $if2(%album%,Singles)/...
        /// When <paramref name="compilationAlbums"/> is provided and the file's album is in that
        /// set, "Various Artists" is used as the folder name (mimicking Picard's compilation
        /// behavior) unless the file already has an AlbumArtist tag.
        /// </summary>
        public static string GetPicardPath(string filePath, string outputDirectory, IReadOnlySet<string>? compilationAlbums = null)
        {
            try
            {
                using (var audioFile = TagLib.File.Create(filePath))
                {
                    var tag = audioFile.Tag;
                    
                    // $if2(%album%,Singles) - prefer album name, fallback to "Singles"
                    string album = !string.IsNullOrWhiteSpace(tag.Album) ? tag.Album : "Singles";

                    // Extract artist following Picard logic:
                    //   1. Use AlbumArtist tag if present.
                    //   2. If the album is flagged as a compilation, use "Various Artists".
                    //   3. Otherwise fall back to the track artist.
                    string artist;
                    if (!string.IsNullOrWhiteSpace(tag.FirstAlbumArtist))
                    {
                        artist = tag.FirstAlbumArtist;
                    }
                    else if (compilationAlbums != null && compilationAlbums.Contains(album))
                    {
                        artist = "Various Artists";
                    }
                    else
                    {
                        artist = !string.IsNullOrWhiteSpace(tag.FirstPerformer) ? tag.FirstPerformer : "Unknown Artist";
                    }
                    
                    uint trackNumber = tag.Track;
                    uint discNumber = tag.Disc;
                    uint totalDiscs = GetTotalDiscs(audioFile, tag);
                    
                    string title = !string.IsNullOrWhiteSpace(tag.Title) ? tag.Title : Path.GetFileNameWithoutExtension(filePath);
                    
                    // Build directory structure: AlbumArtist/Album
                    string albumPath = Path.Combine(outputDirectory, SanitizePath(artist), SanitizePath(album));
                    
                    // Build filename with conditional disc number
                    // $if($gt(%totaldiscs%,1),$num(%discnumber%,2)-,) - prepend disc# only if multi-disc
                    string filename;
                    if (trackNumber > 0)
                    {
                        string trackPart = totalDiscs > 1
                            ? $"{discNumber:D2}-{trackNumber:D2} - {SanitizeFilename(title)}"
                            : $"{trackNumber:D2} - {SanitizeFilename(title)}";
                        filename = trackPart;
                    }
                    else
                    {
                        filename = SanitizeFilename(title);
                    }
                    
                    string extension = Path.GetExtension(filePath).ToLower();
                    filename = $"{filename}{extension}";
                    
                    return Path.Combine(albumPath, filename);
                }
            }
            catch
            {
                // Fallback to original filename structure
                return Path.Combine(outputDirectory, "Unknown", Path.GetFileName(filePath));
            }
        }

        /// <summary>
        /// Attempts to read total disc count from audio file metadata
        /// </summary>
        private static uint GetTotalDiscs(TagLib.File audioFile, Tag tag)
        {
            try
            {
                // Check if TPOS frame exists (ID3v2.4 frame for disc position)
                var id3v2Tag = audioFile.GetTag(TagTypes.Id3v2) as TagLib.Id3v2.Tag;
                if (id3v2Tag != null)
                {
                    var tposFrames = id3v2Tag.GetFrames<TagLib.Id3v2.TextInformationFrame>("TPOS");
                    foreach (var frame in tposFrames)
                    {
                        if (frame != null && frame.Text.Length > 0)
                        {
                            string tposText = frame.Text[0];
                            // TPOS can be "1/2" (current/total) or just "1"
                            if (tposText.Contains("/"))
                            {
                                var parts = tposText.Split('/');
                                if (uint.TryParse(parts[1], out uint total))
                                {
                                    return total;
                                }
                            }
                        }
                    }
                }

                // Fallback to any disc property if available
                // Note: TagLibSharp 2.3.0 may not expose TotalDiscs directly
                return tag.Disc > 1 ? tag.Disc : 0;
            }
            catch
            {
                return 0;
            }
        }

        /// <summary>
        /// Renames a file to Picard-style naming and moves it to the correct directory.
        /// Pass <paramref name="compilationAlbums"/> (from <see cref="DetectCompilationAlbums"/>)
        /// to have multi-artist albums grouped under a "Various Artists" folder.
        /// </summary>
        public static bool RenameToPicardStyle(string filePath, string basePath, IReadOnlySet<string>? compilationAlbums = null)
        {
            try
            {
                string? sourceDirectory = Path.GetDirectoryName(filePath);
                var newPath = GetPicardPath(filePath, basePath, compilationAlbums);
                
                // Create directory if it doesn't exist
                string? directory = Path.GetDirectoryName(newPath);
                if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }
                
                if (File.Exists(newPath) && newPath != filePath)
                {
                    File.Delete(newPath);
                }
                
                // Move file to new location
                if (filePath != newPath)
                {
                    if (File.Exists(filePath))
                    {
                        File.Move(filePath, newPath, overwrite: true);
                    }

                    CleanupEmptyDirectories(sourceDirectory, basePath);
                }
                
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static void CleanupEmptyDirectories(string? startDirectory, string stopAtDirectory)
        {
            if (string.IsNullOrWhiteSpace(startDirectory) || string.IsNullOrWhiteSpace(stopAtDirectory))
            {
                return;
            }

            string current = Path.GetFullPath(startDirectory);
            string stopAt = Path.GetFullPath(stopAtDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            while (current.StartsWith(stopAt, StringComparison.OrdinalIgnoreCase) &&
                   !string.Equals(current, stopAt, StringComparison.OrdinalIgnoreCase))
            {
                if (!Directory.Exists(current))
                {
                    break;
                }

                bool hasFiles = Directory.EnumerateFiles(current).Any();
                bool hasDirectories = Directory.EnumerateDirectories(current).Any();

                if (hasFiles || hasDirectories)
                {
                    break;
                }

                Directory.Delete(current, recursive: false);

                var parent = Directory.GetParent(current);
                if (parent == null)
                {
                    break;
                }

                current = parent.FullName;
            }
        }

        private static string SanitizePath(string path)
        {
            string invalid = new string(Path.GetInvalidPathChars());
            foreach (char c in invalid)
            {
                path = path.Replace(c.ToString(), "");
            }
            return path;
        }

        private static string SanitizeFilename(string filename)
        {
            string invalid = new string(Path.GetInvalidFileNameChars());
            foreach (char c in invalid)
            {
                filename = filename.Replace(c.ToString(), "");
            }
            return filename.Trim();
        }
    }
}
