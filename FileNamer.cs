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
            // normalized album name -> list of (trackArtist, albumArtist)
            var albumGroups = new Dictionary<string, List<(string trackArtist, string albumArtist)>>(StringComparer.OrdinalIgnoreCase);

            // Helper: token set for fuzzy grouping
            static HashSet<string> Tokenize(string s)
            {
                if (string.IsNullOrWhiteSpace(s)) return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var parts = System.Text.RegularExpressions.Regex.Split(s.ToLowerInvariant(), "[^a-z0-9]+")
                    .Where(p => p.Length > 1).ToArray();
                return new HashSet<string>(parts, StringComparer.OrdinalIgnoreCase);
            }

            foreach (var filePath in filePaths)
            {
                try
                {
                    using var audioFile = TagLib.File.Create(filePath);
                    var tag = audioFile.Tag;
                    string album = !string.IsNullOrWhiteSpace(tag.Album) ? tag.Album : "Singles";
                    string albumKey = NormalizeAlbumKey(album);
                    string trackArtist = tag.FirstPerformer ?? string.Empty;
                    string albumArtist = tag.FirstAlbumArtist ?? string.Empty;

                    if (!albumGroups.TryGetValue(albumKey, out var list))
                    {
                        list = new List<(string, string)>();
                        albumGroups[albumKey] = list;
                    }
                    list.Add((trackArtist, albumArtist));
                }
                catch
                {
                    // Skip files whose tags cannot be read.
                }
            }

            // Now perform fuzzy clustering of album keys so near-identical album tags are treated as one album.
            var keys = albumGroups.Keys.ToList();
            var tokensMap = keys.ToDictionary(k => k, k => Tokenize(k), StringComparer.OrdinalIgnoreCase);
            var clusters = new List<List<string>>();
            var assigned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < keys.Count; i++)
            {
                var k = keys[i];
                if (assigned.Contains(k)) continue;
                var cluster = new List<string> { k };
                assigned.Add(k);
                var t1 = tokensMap[k];

                for (int j = i + 1; j < keys.Count; j++)
                {
                    var k2 = keys[j];
                    if (assigned.Contains(k2)) continue;
                    var t2 = tokensMap[k2];
                    if (t1.Count == 0 || t2.Count == 0) continue;
                    var inter = t1.Intersect(t2, StringComparer.OrdinalIgnoreCase).Count();
                    var union = t1.Union(t2, StringComparer.OrdinalIgnoreCase).Count();
                    double jaccard = union == 0 ? 0.0 : (double)inter / union;
                    if (jaccard >= 0.55)
                    {
                        cluster.Add(k2);
                        assigned.Add(k2);
                    }
                }

                clusters.Add(cluster);
            }

            var compilations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Evaluate each cluster for compilation characteristics
            foreach (var cluster in clusters)
            {
                var combinedEntries = new List<(string trackArtist, string albumArtist)>();
                foreach (var key in cluster)
                {
                    if (albumGroups.TryGetValue(key, out var list))
                        combinedEntries.AddRange(list);
                }

                int distinctAlbumArtists = combinedEntries
                    .Select(e => e.albumArtist.Trim())
                    .Where(a => !string.IsNullOrWhiteSpace(a))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count();

                bool allHaveAlbumArtist = combinedEntries.All(e => !string.IsNullOrWhiteSpace(e.albumArtist));

                int distinctArtists = combinedEntries
                    .Select(e => e.trackArtist.Trim())
                    .Where(a => !string.IsNullOrWhiteSpace(a))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count();

                bool consistentAlbumArtist = allHaveAlbumArtist && distinctAlbumArtists == 1;

                if (!consistentAlbumArtist && distinctArtists > 1)
                {
                    // mark all member keys as compilation albums
                    foreach (var key in cluster)
                        compilations.Add(key);
                }
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
                    string albumKey = NormalizeAlbumKey(album);

                    // Extract artist following Picard logic:
                    //   1. Use AlbumArtist tag if present.
                    //   2. If the album is flagged as a compilation, use "Various Artists".
                    //   3. Otherwise fall back to the track artist.
                    string artist;
                    if (!string.IsNullOrWhiteSpace(tag.FirstAlbumArtist))
                    {
                        artist = tag.FirstAlbumArtist;
                    }
                    else if (compilationAlbums != null && compilationAlbums.Contains(albumKey))
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

        private static string NormalizeAlbumKey(string album)
        {
            if (string.IsNullOrWhiteSpace(album))
            {
                return "singles";
            }

            // Best-effort tolerant normalization:
            // - remove parenthetical phrases, years, and common edition markers
            // - strip punctuation, accents, and extra whitespace
            // - lowercase
            try
            {
                string s = album.Trim();

                // remove parenthetical content e.g. "Album Name (Deluxe)"
                s = System.Text.RegularExpressions.Regex.Replace(s, "\\([^)]*\\)", "");

                // remove bracketed content [Bonus Tracks]
                s = System.Text.RegularExpressions.Regex.Replace(s, "\\[[^]]*\\]", "");

                // remove common edition/year suffixes like "- 1999", "(2010 Remaster)", "Deluxe Edition"
                s = System.Text.RegularExpressions.Regex.Replace(s, "\\b(edition|deluxe|remaster(ed)?|expanded|bonus|anniversary)\\b", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                s = System.Text.RegularExpressions.Regex.Replace(s, "\\b(19|20)\\d{2}\\b", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

                // remove punctuation except letters/numbers/space (allow word chars, whitespace and dash)
                s = System.Text.RegularExpressions.Regex.Replace(s, @"[^\w\s-]", "");

                // Normalize diacritics: decompose and remove diacritic marks
                var normalized = s.Normalize(System.Text.NormalizationForm.FormD);
                var sb = new System.Text.StringBuilder();
                foreach (var ch in normalized)
                {
                    var uc = System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch);
                    if (uc != System.Globalization.UnicodeCategory.NonSpacingMark)
                        sb.Append(ch);
                }
                s = sb.ToString().Normalize(System.Text.NormalizationForm.FormC);

                // remove leading articles
                s = System.Text.RegularExpressions.Regex.Replace(s, "^(the |a |an )", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

                // collapse separators and whitespace
                s = System.Text.RegularExpressions.Regex.Replace(s, "[-_]+", " ");
                s = System.Text.RegularExpressions.Regex.Replace(s, "\\s+", " ").Trim();

                if (string.IsNullOrWhiteSpace(s)) return "singles";
                return s.ToLowerInvariant();
            }
            catch
            {
                return album.Trim().ToLowerInvariant();
            }
        }
    }
}
