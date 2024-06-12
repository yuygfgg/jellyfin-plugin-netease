using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using Jellyfin.Plugin.LrcLib.Models;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Lyrics;
using MediaBrowser.Model.Lyrics;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.LrcLib;

/// <summary>
/// Lyric provider for LrcLib.
/// </summary>
public class LrcLibProvider : ILyricProvider
{
    private const string BaseUrl = "https://music.163.com";
    private const string SyncedSuffix = "synced";
    private const string PlainSuffix = "plain";
    private const string SyncedFormat = "lrc";
    private const string PlainFormat = "txt";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<LrcLibProvider> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="LrcLibProvider"/> class.
    /// </summary>
    /// <param name="httpClientFactory">Instance of the <see cref="IHttpClientFactory"/> interface.</param>
    /// <param name="logger">Instance of the <see cref="ILogger{LrcLibProvider}"/>.</param>
    public LrcLibProvider(IHttpClientFactory httpClientFactory, ILogger<LrcLibProvider> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    private static bool UseStrictSearch => LrcLibPlugin.Instance?.Configuration.UseStrictSearch ?? true;

    private static bool ExcludeArtistName => LrcLibPlugin.Instance?.Configuration.ExcludeArtistName ?? false;

    private static bool ExcludeAlbumName => LrcLibPlugin.Instance?.Configuration.ExcludeAlbumName ?? false;

    /// <inheritdoc />
    public string Name => LrcLibPlugin.Instance!.Name;

    /// <inheritdoc />
    public async Task<IEnumerable<RemoteLyricInfo>> SearchAsync(
        LyricSearchRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            return UseStrictSearch
                ? await GetExactMatch(request, cancellationToken).ConfigureAwait(false)
                : await GetFuzzyMatch(request, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogDebug(
                ex,
                "Unable to get results for {Artist} - {Album} - {Song}",
                request.ArtistNames?[0],
                request.AlbumName,
                request.SongName);
            return Enumerable.Empty<RemoteLyricInfo>();
        }
    }

    /// <inheritdoc />
    public async Task<LyricResponse?> GetLyricsAsync(string id, CancellationToken cancellationToken)
    {
        var splitId = id.Split('_', 2);

        try
        {
            var requestUri = new UriBuilder(BaseUrl)
            {
                Path = $"/api/song/lyric",
                Query = $"id={splitId[0]}&lv=-1&tv=-1"
            };

            var httpClient = _httpClientFactory.CreateClient(NamedClient.Default);
            httpClient.DefaultRequestHeaders.Referrer = new Uri("https://music.163.com");
            var response = await httpClient.GetFromJsonAsync<NeteaseLyricResponse>(requestUri.Uri, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (response is null)
            {
                throw new ResourceNotFoundException("Unable to get results for id {Id}");
            }

            if (string.Equals(splitId[1], SyncedSuffix, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrEmpty(response.lrc.lyric))
            {
                var stream = new MemoryStream(Encoding.UTF8.GetBytes(response.lrc.lyric));
                return new LyricResponse
                {
                    Format = SyncedFormat,
                    Stream = stream
                };
            }

            if (string.Equals(splitId[1], PlainSuffix, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrEmpty(response.tlyric.lyric))
            {
                var stream = new MemoryStream(Encoding.UTF8.GetBytes(response.tlyric.lyric));
                return new LyricResponse
                {
                    Format = PlainFormat,
                    Stream = stream
                };
            }

            throw new ResourceNotFoundException("Unable to get results for id {Id}");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogDebug(
                ex,
                "Unable to get results for id {Id}",
                id);
            throw new ResourceNotFoundException("Unable to get results for id {Id}");
        }
    }

    private async Task<IEnumerable<RemoteLyricInfo>> GetExactMatch(
    LyricSearchRequest request,
    CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(request.SongName))
        {
            _logger.LogInformation("Song name is required");
            return Enumerable.Empty<RemoteLyricInfo>();
        }

        string artist;
        if (request.ArtistNames is not null
            && request.ArtistNames.Count > 0)
        {
            artist = request.ArtistNames[0];
        }
        else
        {
            _logger.LogInformation("Artist name is required");
            return Enumerable.Empty<RemoteLyricInfo>();
        }

        if (string.IsNullOrEmpty(request.AlbumName))
        {
            _logger.LogInformation("Album name is required");
            return Enumerable.Empty<RemoteLyricInfo>();
        }

        if (request.Duration is null)
        {
            _logger.LogInformation("Duration is required");
            return Enumerable.Empty<RemoteLyricInfo>();
        }

        var queryStringBuilder = new StringBuilder()
            .Append("s=")
            .Append(HttpUtility.UrlEncode(request.SongName))
            .Append("&type=1")
            .Append("&limit=50");
        var requestUri = new UriBuilder(BaseUrl)
        {
            Path = "/api/search/get/web",
            Query = queryStringBuilder.ToString()
        };

        var httpClient = _httpClientFactory.CreateClient(NamedClient.Default);
        httpClient.DefaultRequestHeaders.Referrer = new Uri("https://music.163.com");

        var response = await httpClient.GetFromJsonAsync<NeteaseSearchResponse>(requestUri.Uri, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (response is null)
        {
            return Enumerable.Empty<RemoteLyricInfo>();
        }

        var songs = response.result.songs.Where(song => Math.Abs(song.duration / 1000 - request.Duration.Value.TotalSeconds) <= 3).ToList();
        return await GetLyricsFromSongs(songs, cancellationToken);
    }

    private async Task<IEnumerable<RemoteLyricInfo>> GetFuzzyMatch(
        LyricSearchRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(request.SongName))
        {
            _logger.LogInformation("Song name is required");
            return Enumerable.Empty<RemoteLyricInfo>();
        }

        var queryStringBuilder = new StringBuilder()
            .Append("s=")
            .Append(HttpUtility.UrlEncode(request.SongName))
            .Append("&type=1")
            .Append("&limit=50");
        if (!ExcludeArtistName)
        {
            string artist;
            if (request.ArtistNames is not null
                && request.ArtistNames.Count > 0)
            {
                artist = request.ArtistNames[0];
            }
            else
            {
                _logger.LogInformation("Artist name is required");
                return Enumerable.Empty<RemoteLyricInfo>();
            }

            queryStringBuilder
                .Append("&artist_name=")
                .Append(HttpUtility.UrlEncode(artist));
        }

        if (!ExcludeAlbumName)
        {
            if (string.IsNullOrEmpty(request.AlbumName))
            {
                _logger.LogInformation("Album name is required");
                return Enumerable.Empty<RemoteLyricInfo>();
            }

            queryStringBuilder
                .Append("&album_name=")
                .Append(HttpUtility.UrlEncode(request.AlbumName));
        }

        var requestUri = new UriBuilder(BaseUrl)
        {
            Path = "/api/search/get/web",
            Query = queryStringBuilder.ToString()
        };

        var httpClient = _httpClientFactory.CreateClient(NamedClient.Default);
        httpClient.DefaultRequestHeaders.Referrer = new Uri("https://music.163.com");

        var response = await httpClient.GetFromJsonAsync<NeteaseSearchResponse>(requestUri.Uri, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (response is null)
        {
            return Enumerable.Empty<RemoteLyricInfo>();
        }

        var songs = response.result.songs.ToList();
        return await GetLyricsFromSongs(songs, cancellationToken);
    }

    private async Task<IEnumerable<RemoteLyricInfo>> GetLyricsFromSongs(IEnumerable<NeteaseSong> songs, CancellationToken cancellationToken)
    {
        var results = new List<RemoteLyricInfo>();
        foreach (var song in songs)
        {
            var lyrics_content, trans_lyrics_content = await download_lyrics(song.id, cancellationToken).ConfigureAwait(false);

            if (lyrics_content)
            {
                lrc_dict, unformatted_lines = ParseLyrics(lyrics_content);
                if (lrc_dict.Count >= 5)
                {
                    tlyric_dict, _ = ParseLyrics(trans_lyrics_content ?? string.Empty);
                    var merged = MergeLyrics(lrc_dict, tlyric_dict, unformatted_lines);
                    results.Add(new RemoteLyricInfo
                    {
                        Id = $"{song.id}_{SyncedSuffix}",
                        ProviderName = Name,
                        Metadata = new LyricMetadata
                        {
                            Album = song.album.name,
                            Artist = string.Join(", ", song.artists.Select(artist => artist.name)),
                            Title = song.name,
                            Length = TimeSpan.FromMilliseconds(song.duration).Ticks,
                            IsSynced = true
                        },
                        Lyrics = new LyricResponse
                        {
                            Format = SyncedFormat,
                            Stream = new MemoryStream(Encoding.UTF8.GetBytes(merged))
                        }
                    });
                }
            }
        }

        return results;
    }

    private List<RemoteLyricInfo> GetRemoteLyrics(NeteaseSong song)
    {
        var results = new List<RemoteLyricInfo>();

        if (!string.IsNullOrEmpty(song.lrc.lyric))
        {
            var stream = new MemoryStream(Encoding.UTF8.GetBytes(song.lrc.lyric));
            results.Add(new RemoteLyricInfo
            {
                Id = $"{song.id}_{SyncedSuffix}",
                ProviderName = Name,
                Metadata = new LyricMetadata
                {
                    Album = song.album.name,
                    Artist = string.Join(", ", song.artists.Select(artist => artist.name)),
                    Title = song.name,
                    Length = TimeSpan.FromMilliseconds(song.duration).Ticks,
                    IsSynced = true
                },
                Lyrics = new LyricResponse
                {
                    Format = SyncedFormat,
                    Stream = stream
                }
            });
        }

        if (!string.IsNullOrEmpty(song.tlyric.lyric))
        {
            var stream = new MemoryStream(Encoding.UTF8.GetBytes(song.tlyric.lyric));
            results.Add(new RemoteLyricInfo
            {
                Id = $"{song.id}_{PlainSuffix}",
                ProviderName = Name,
                Metadata = new LyricMetadata
                {
                    Album = song.album.name,
                    Artist = string.Join(", ", song.artists.Select(artist => artist.name)),
                    Title = song.name,
                    Length = TimeSpan.FromMilliseconds(song.duration).Ticks,
                    IsSynced = false
                },
                Lyrics = new LyricResponse
                {
                    Format = PlainFormat,
                    Stream = stream
                }
            });
        }

        return results;
    }

    private Dictionary<string, string> ParseLyrics(string lyrics)
    {
        var lyricsDict = new Dictionary<string, string>();
        var pattern = new Regex(@"\[(\d{2}):(\d{2})([.:]\d{2,3})?\](.*)");

        foreach (var line in lyrics.Split('\n'))
        {
            var match = pattern.Match(line);
            if (match.Success)
            {
                var minute = match.Groups[1].Value;
                var second = match.Groups[2].Value;
                var millisecond = match.Groups[3].Value ?? ".000";
                millisecond = millisecond.Replace(":", ".");
                var lyric = match.Groups[4].Value;
                var timeStamp = $"[{minute}:{second}{millisecond}]";
                lyricsDict[timeStamp] = lyric;
            }
        }

        return lyricsDict;
    }

    private string MergeLyrics(Dictionary<string, string> lrcDict, Dictionary<string, string> tlyricDict, List<string> unformattedLines)
    {
        var mergedLyrics = new StringBuilder();
        foreach (var line in unformattedLines)
        {
            mergedLyrics.AppendLine(line);
        }

        var allTimeStamps = lrcDict.Keys.Union(tlyricDict.Keys).OrderBy(t => t);
        foreach (var timeStamp in allTimeStamps)
        {
            var originalLine = lrcDict.ContainsKey(timeStamp) ? lrcDict[timeStamp] : string.Empty;
            var translatedLine = tlyricDict.ContainsKey(timeStamp) ? tlyricDict[timeStamp] : string.Empty;
            mergedLyrics.AppendLine($"{timeStamp}{originalLine}");
            if (!string.IsNullOrEmpty(translatedLine))
            {
                mergedLyrics.AppendLine($"{timeStamp}{translatedLine}");
            }
        }

        return mergedLyrics.ToString();
    }
}
