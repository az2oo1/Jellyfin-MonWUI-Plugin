using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Security;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.JMSFusion.Core;

public sealed class TrailerAutomationService
{
    public const string DownloaderStep = "trailers.sh";
    public const string UrlNfoStep = "trailersurl.sh";

    private const string DefaultIncludeTypes = "Movie,Series,Season,Episode";
    private const int DefaultPageSize = 200;
    private const double DefaultSleepSecs = 1.0;
    private const string DefaultPreferredLang = "tr-TR";
    private const string DefaultFallbackLang = "en-US";
    private const string DefaultIncludeLangsWide = "tr,en,hi,de,ru,fr,it,es,ar,fa,pt,zh,ja,ko,nl,pl,sv,cs,uk,el,null";
    private const string DefaultWorkDirName = "trailers-dl";
    private const string DefaultToolDirName = "jmsfusion-tools";
    private const long BetterMinSizeDelta = 1_048_576;
    private const double BetterMinDurationDelta = 3d;
    private const long MinTrailerBytes = 2L * 1024L * 1024L;
    private const double MinTrailerDurationSeconds = 20d;
    private const long MinFreeMb = 1024;

    private static readonly HttpClient Http = CreateHttpClient();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public sealed class TrailerRunOptions
    {
        public string JfBase { get; init; } = "http://localhost:8096";
        public string JfApiKey { get; init; } = "CHANGE_ME";
        public string TmdbApiKey { get; init; } = "CHANGE_ME";
        public string PreferredLang { get; init; } = DefaultPreferredLang;
        public string FallbackLang { get; init; } = DefaultFallbackLang;
        public string IncludeTypes { get; init; } = DefaultIncludeTypes;
        public int PageSize { get; init; } = DefaultPageSize;
        public double SleepSecs { get; init; } = DefaultSleepSecs;
        public string? JfUserId { get; init; }
        public string OverwritePolicy { get; init; } = "skip";
        public int EnableThemeLink { get; init; }
        public string ThemeLinkMode { get; init; } = "symlink";
    }

    public sealed record TrailerStepResult(string Script, int ExitCode, string Stdout, string Stderr);

    private enum DownloadOutcome
    {
        Ok,
        Fail,
        Skip
    }

    private enum NfoOutcome
    {
        Ok,
        SkipHasTrailer,
        NotFound,
        FailWrite,
        FailRefresh,
        Unsupported,
        NoPath,
        NoTmdb,
        Misc
    }

    private sealed record TrailerCandidate(string Site, string Key);

    private sealed class StepLogger
    {
        private readonly StringBuilder _stdout = new();
        private readonly StringBuilder _stderr = new();
        private readonly Action<string, bool>? _onLine;

        public StepLogger(Action<string, bool>? onLine)
        {
            _onLine = onLine;
        }

        public void Out(string line)
        {
            line ??= string.Empty;
            _stdout.AppendLine(line);
            _onLine?.Invoke(line, false);
        }

        public void Err(string line)
        {
            line ??= string.Empty;
            _stderr.AppendLine(line);
            _stdout.AppendLine(line);
            _onLine?.Invoke(line, false);
        }

        public string Stdout => _stdout.ToString();
        public string Stderr => _stderr.ToString();
    }

    private sealed class StepContext
    {
        public required TrailerRunOptions Options { get; init; }
        public required StepLogger Log { get; init; }
        public string PreferredIso639 => GetIso639(Options.PreferredLang);
        public string FallbackIso639 => GetIso639(Options.FallbackLang);
        public string IncludeTypes => string.IsNullOrWhiteSpace(Options.IncludeTypes) ? DefaultIncludeTypes : Options.IncludeTypes;
        public int PageSize => Options.PageSize > 0 ? Options.PageSize : DefaultPageSize;
        public double SleepSecs => Options.SleepSecs >= 0 ? Options.SleepSecs : DefaultSleepSecs;
        public string WorkDir => Path.Combine(Path.GetTempPath(), DefaultWorkDirName);
    }

    private sealed record JellyfinItemsResponse(
        int TotalRecordCount,
        List<JellyfinItem>? Items);

    private sealed record JellyfinItem(
        string? Id,
        string? Type,
        string? Name,
        int? ProductionYear,
        string? Path,
        Dictionary<string, string>? ProviderIds,
        string? SeriesId,
        int? IndexNumber,
        int? ParentIndexNumber,
        List<JellyfinMediaSource>? MediaSources);

    private sealed record JellyfinMediaSource(string? Path);

    private sealed record JellyfinUser(string? Id, JellyfinUserPolicy? Policy);
    private sealed record JellyfinUserPolicy(bool IsAdministrator);

    private sealed record TmdbFindResponse(
        [property: JsonPropertyName("movie_results")] List<TmdbFindItem>? MovieResults,
        [property: JsonPropertyName("tv_results")] List<TmdbFindItem>? TvResults);

    private sealed record TmdbFindItem(int Id);

    private sealed record TmdbVideosResponse(List<TmdbVideo>? Results);

    private sealed record TmdbVideo(string? Site, string? Type, string? Key);

    private sealed record ProcessRunResult(int ExitCode, string Stdout, string Stderr);

    private static HttpClient CreateHttpClient()
    {
        return new HttpClient
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
    }

    public async Task<TrailerStepResult> RunStepAsync(
        string stepName,
        TrailerRunOptions options,
        Action<string, bool>? onLine = null,
        CancellationToken ct = default)
    {
        var logger = new StepLogger(onLine);
        try
        {
            var normalized = NormalizeOptions(options);
            return stepName switch
            {
                DownloaderStep => await RunDownloaderAsync(normalized, logger, ct).ConfigureAwait(false),
                UrlNfoStep => await RunUrlNfoAsync(normalized, logger, ct).ConfigureAwait(false),
                _ => new TrailerStepResult(stepName, 1, $"[HATA] Bilinmeyen görev: {stepName}{Environment.NewLine}", string.Empty)
            };
        }
        catch (OperationCanceledException)
        {
            logger.Out("[WARN] İş iptal edildi.");
            return new TrailerStepResult(stepName, 130, logger.Stdout, logger.Stderr);
        }
        catch (Exception ex)
        {
            logger.Err($"[HATA] {ex.Message}");
            return new TrailerStepResult(stepName, 1, logger.Stdout, logger.Stderr);
        }
    }

    public bool HasCommand(string commandName)
    {
        if (string.Equals(commandName, "yt-dlp", StringComparison.OrdinalIgnoreCase))
        {
            return !string.IsNullOrWhiteSpace(ResolveYtDlpCommand());
        }

        return CommandExists(commandName);
    }

    private async Task<TrailerStepResult> RunDownloaderAsync(TrailerRunOptions options, StepLogger logger, CancellationToken ct)
    {
        var ctx = new StepContext { Options = options, Log = logger };
        if (!ValidateCommonConfig(ctx, requireTmdb: true, out var commonError))
        {
            logger.Err(commonError);
            return new TrailerStepResult(DownloaderStep, 1, logger.Stdout, logger.Stderr);
        }

        if (!TryNormalizeOverwritePolicy(options.OverwritePolicy, out var overwritePolicy))
        {
            logger.Err($"[HATA] OVERWRITE_POLICY geçersiz: {options.OverwritePolicy} (skip|replace|if-better)");
            return new TrailerStepResult(DownloaderStep, 2, logger.Stdout, logger.Stderr);
        }

        var ytDlpCommand = ResolveYtDlpCommand();
        if (string.IsNullOrWhiteSpace(ytDlpCommand))
        {
            logger.Err("Hata: yt-dlp kurulu değil.");
            return new TrailerStepResult(DownloaderStep, 1, logger.Stdout, logger.Stderr);
        }

        var hasFfprobe = CommandExists("ffprobe");
        if (!hasFfprobe)
        {
            logger.Err("Uyarı: ffprobe yok; süre/boyut kontrolleri sınırlı olur.");
        }

        if (!TryEnsureDirectory(ctx.WorkDir, out var workDirError))
        {
            logger.Err($"[HATA] WORK_DIR oluşturulamadı: {ctx.WorkDir}");
            if (!string.IsNullOrWhiteSpace(workDirError))
            {
                logger.Err($"[WARN] {workDirError}");
            }
            return new TrailerStepResult(DownloaderStep, 1, logger.Stdout, logger.Stderr);
        }

        var resolvedUserId = await ResolveUserIdAsync(ctx, ct).ConfigureAwait(false);
        var seenDirs = new HashSet<string>(StringComparer.Ordinal);
        var seriesTmdbCache = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var itemDetailsCache = new Dictionary<string, JellyfinItem?>(StringComparer.OrdinalIgnoreCase);
        var imdbMapCache = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var trailerCache = new Dictionary<string, IReadOnlyList<TrailerCandidate>>(StringComparer.OrdinalIgnoreCase);

        var start = 0;
        var processed = 0;
        var ok = 0;
        var fail = 0;
        var skip = 0;
        var total = 0;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var page = await GetItemsPageAsync(
                ctx,
                resolvedUserId,
                userScoped: true,
                fields: "Path,ProviderIds,ProductionYear,MediaSources",
                start: start,
                limit: ctx.PageSize,
                ct: ct).ConfigureAwait(false);

            total = page.TotalRecordCount;
            logger.Out($"JMSF::TOTAL={total}");

            foreach (var item in page.Items ?? Enumerable.Empty<JellyfinItem>())
            {
                ct.ThrowIfCancellationRequested();

                var path = FirstNonEmpty(item.Path, item.MediaSources?.FirstOrDefault()?.Path);
                if (string.IsNullOrWhiteSpace(path))
                {
                    continue;
                }

                var outcome = await ProcessDownloadItemAsync(
                    ctx,
                    resolvedUserId,
                    item,
                    path,
                    ytDlpCommand,
                    overwritePolicy,
                    hasFfprobe,
                    seenDirs,
                    seriesTmdbCache,
                    itemDetailsCache,
                    imdbMapCache,
                    trailerCache,
                    ct).ConfigureAwait(false);

                processed++;
                logger.Out($"JMSF::DONE={processed}");

                switch (outcome)
                {
                    case DownloadOutcome.Ok:
                        ok++;
                        break;
                    case DownloadOutcome.Skip:
                        skip++;
                        break;
                    default:
                        fail++;
                        break;
                }
            }

            start += ctx.PageSize;
            if (start >= total)
            {
                break;
            }
        }

        logger.Out("[INFO] Geçici dosyalar temizleniyor...");
        CleanupTemporaryFiles(seenDirs, ctx.WorkDir);
        logger.Out(string.Empty);
        logger.Out($"BİTTİ: işlenen={processed}");
        logger.Out($"ÖZET -> indirilen={ok}, başarısız={fail}, atlanan(zaten vardı)={skip}");

        return new TrailerStepResult(DownloaderStep, 0, logger.Stdout, logger.Stderr);
    }

    private async Task<TrailerStepResult> RunUrlNfoAsync(TrailerRunOptions options, StepLogger logger, CancellationToken ct)
    {
        var ctx = new StepContext { Options = options, Log = logger };
        if (!ValidateCommonConfig(ctx, requireTmdb: true, out var commonError))
        {
            logger.Err(commonError);
            return new TrailerStepResult(UrlNfoStep, 1, logger.Stdout, logger.Stderr);
        }

        var resolvedUserId = await ResolveUserIdAsync(ctx, ct).ConfigureAwait(false);
        var seriesTmdbCache = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var itemDetailsCache = new Dictionary<string, JellyfinItem?>(StringComparer.OrdinalIgnoreCase);
        var imdbMapCache = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var trailerCache = new Dictionary<string, IReadOnlyList<TrailerCandidate>>(StringComparer.OrdinalIgnoreCase);

        var start = 0;
        var totalProcessed = 0;
        var totalRecords = 0;
        var ok = 0;
        var skipHas = 0;
        var notFound = 0;
        var failWrite = 0;
        var failRefresh = 0;
        var unsupported = 0;
        var noPath = 0;
        var noTmdb = 0;
        var misc = 0;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var page = await GetItemsPageAsync(
                ctx,
                resolvedUserId,
                userScoped: false,
                fields: "Path,ProviderIds,ProductionYear",
                start: start,
                limit: ctx.PageSize,
                ct: ct).ConfigureAwait(false);

            totalRecords = page.TotalRecordCount;
            if (start == 0)
            {
                logger.Out($"JMSF::TOTAL={totalRecords}");
            }

            foreach (var item in page.Items ?? Enumerable.Empty<JellyfinItem>())
            {
                ct.ThrowIfCancellationRequested();
                var path = item.Path;
                var name = item.Name ?? "(adsiz)";

                if (string.IsNullOrWhiteSpace(path))
                {
                    logger.Out($"[ATLA] Yol yok: {name}");
                    noPath++;
                    continue;
                }

                totalProcessed++;
                logger.Out($"JMSF::DONE={totalProcessed}");

                var outcome = await ProcessUrlNfoItemAsync(
                    ctx,
                    resolvedUserId,
                    item,
                    path,
                    seriesTmdbCache,
                    itemDetailsCache,
                    imdbMapCache,
                    trailerCache,
                    ct).ConfigureAwait(false);

                switch (outcome)
                {
                    case NfoOutcome.Ok:
                        ok++;
                        break;
                    case NfoOutcome.SkipHasTrailer:
                        skipHas++;
                        break;
                    case NfoOutcome.NotFound:
                        notFound++;
                        break;
                    case NfoOutcome.FailWrite:
                        failWrite++;
                        break;
                    case NfoOutcome.FailRefresh:
                        failRefresh++;
                        break;
                    case NfoOutcome.Unsupported:
                        unsupported++;
                        break;
                    case NfoOutcome.NoTmdb:
                        noTmdb++;
                        break;
                    case NfoOutcome.NoPath:
                        noPath++;
                        break;
                    default:
                        misc++;
                        break;
                }
            }

            start += ctx.PageSize;
            if (start >= totalRecords)
            {
                break;
            }
        }

        logger.Out(string.Empty);
        logger.Out("===== ÖZET =====");
        logger.Out($"Toplam işlenen öğe      : {totalProcessed}");
        logger.Out($"Başarılı (NFO eklendi)  : {ok}");
        logger.Out($"Atlandı (zaten vardı)   : {skipHas}");
        logger.Out($"Trailer bulunamadı      : {notFound}");
        logger.Out($"NFO yazma hatası        : {failWrite}");
        logger.Out($"Refresh hatası          : {failRefresh}");
        logger.Out($"TMDb ID yok             : {noTmdb}");
        logger.Out($"Yol (Path) yok          : {noPath}");
        logger.Out($"Desteklenmeyen tür      : {unsupported}");
        logger.Out($"Diğer/çeşitli           : {misc}");
        logger.Out("========================");

        return new TrailerStepResult(UrlNfoStep, 0, logger.Stdout, logger.Stderr);
    }

    private async Task<DownloadOutcome> ProcessDownloadItemAsync(
        StepContext ctx,
        string? resolvedUserId,
        JellyfinItem item,
        string path,
        string ytDlpCommand,
        string overwritePolicy,
        bool hasFfprobe,
        HashSet<string> seenDirs,
        Dictionary<string, string?> seriesTmdbCache,
        Dictionary<string, JellyfinItem?> itemDetailsCache,
        Dictionary<string, string?> imdbMapCache,
        Dictionary<string, IReadOnlyList<TrailerCandidate>> trailerCache,
        CancellationToken ct)
    {
        var itemId = item.Id ?? string.Empty;
        var itemType = item.Type ?? string.Empty;
        var name = item.Name ?? "(adsiz)";
        var year = item.ProductionYear?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        var dir = ResolveItemDirectory(path, itemType);
        var outFile = Path.Combine(dir, "trailer.mp4");
        seenDirs.Add(dir);

        if (!CheckDirectoryWritable(dir))
        {
            ctx.Log.Out($"[ATLA] Yazılamayan klasör, atlanıyor: {dir}  ->  {name} ({year})");
            return DownloadOutcome.Skip;
        }

        var compareAfter = false;
        if (File.Exists(outFile))
        {
            switch (overwritePolicy)
            {
                case "skip":
                    if (ctx.Options.EnableThemeLink == 1)
                    {
                        await EnsureBackdropsThemeAsync(dir, outFile, ctx.Log, ctx.Options.ThemeLinkMode, ct).ConfigureAwait(false);
                        ctx.Log.Out($"[ATLA] Zaten var: {outFile}  -> theme.mp4 kuruldu/korundu.");
                    }
                    else
                    {
                        ctx.Log.Out($"[ATLA] Zaten var: {outFile}  ->  {name} ({year})");
                    }
                    return DownloadOutcome.Skip;
                case "replace":
                    ctx.Log.Out($"[BİLGİ] Üzerine yazılacak: {outFile}");
                    break;
                case "if-better":
                    ctx.Log.Out("[BİLGİ] if-better modu: karşılaştırma için indirilecek.");
                    compareAfter = true;
                    break;
            }
        }

        var tmdb = GetProviderId(item.ProviderIds, "Tmdb", "MovieDb");
        var imdb = GetProviderId(item.ProviderIds, "Imdb");

        ctx.Log.Out($"[DEBUG] İşleniyor: {name} (IMDb: {imdb ?? string.Empty}, TMDb: {tmdb ?? string.Empty}, Tür: {itemType})");

        var seriesContext = await ResolveSeriesContextAsync(
            ctx,
            resolvedUserId,
            item,
            itemType,
            tmdb,
            path,
            seriesTmdbCache,
            itemDetailsCache,
            ct).ConfigureAwait(false);

        IReadOnlyList<TrailerCandidate> candidates;
        if (string.Equals(itemType, "Movie", StringComparison.OrdinalIgnoreCase))
        {
            var tmdbId = tmdb;
            if (string.IsNullOrWhiteSpace(tmdbId) && !string.IsNullOrWhiteSpace(imdb))
            {
                tmdbId = await ResolveMovieTmdbFromImdbAsync(ctx, imdb!, imdbMapCache, ct).ConfigureAwait(false);
            }

            if (string.IsNullOrWhiteSpace(tmdbId))
            {
                ctx.Log.Out($"[ATLA] TMDb ID yok: {name}");
                return DownloadOutcome.Fail;
            }

            candidates = await GetMovieCandidatesAsync(ctx, tmdbId!, trailerCache, ct).ConfigureAwait(false);
        }
        else if (itemType is "Series" or "Season" or "Episode")
        {
            if (string.IsNullOrWhiteSpace(seriesContext.SeriesTmdb))
            {
                ctx.Log.Out($"[ATLA] Series TMDb yok: {name}");
                return DownloadOutcome.Fail;
            }

            candidates = await GetTvCandidatesAsync(
                ctx,
                seriesContext.SeriesTmdb!,
                seriesContext.SeasonNumber,
                seriesContext.EpisodeNumber,
                trailerCache,
                ct).ConfigureAwait(false);
        }
        else
        {
            ctx.Log.Out($"[ATLA] Tür desteklenmiyor: {itemType} - {name}");
            return DownloadOutcome.Fail;
        }

        var tmpPath = Path.Combine(ctx.WorkDir, $"{SanitizeFileName(itemId)}.tmp.mp4");
        var tried = 0;

        foreach (var candidate in candidates)
        {
            ct.ThrowIfCancellationRequested();
            tried++;
            ctx.Log.Out($"[DEBUG] Denenen #{tried}: {candidate.Site}:{candidate.Key}");

            var freeMbDest = GetFreeMb(dir);
            if (freeMbDest < MinFreeMb)
            {
                ctx.Log.Out($"[WARN] Hedefte yetersiz boş alan: {freeMbDest} MiB (< {MinFreeMb} MiB). Atlanıyor: {name} ({year})");
                continue;
            }

            var freeMbWork = GetFreeMb(ctx.WorkDir);
            if (freeMbWork < MinFreeMb)
            {
                ctx.Log.Out($"[WARN] Çalışma klasöründe yetersiz boş alan: {freeMbWork} MiB (< {MinFreeMb} MiB). Atlanıyor: {name} ({year})");
                continue;
            }

            TryDeleteFile(tmpPath);

            ctx.Log.Out($"[INDIR] {name} ({year}) -> {outFile}  [{candidate.Site}:{candidate.Key}] (best mp4)");
            var url = candidate.Site == "youtube"
                ? $"https://www.youtube.com/watch?v={candidate.Key}"
                : $"https://vimeo.com/{candidate.Key}";

            var ytdlp = await RunProcessAsync(
                ytDlpCommand,
                [
                    "--force-ipv4",
                    "--no-part",
                    "--no-progress",
                    "--no-playlist",
                    "--merge-output-format", "mp4",
                    "-f", "bestvideo[ext=mp4]+bestaudio[ext=m4a]/best[ext=mp4]/best",
                    "-o", tmpPath,
                    url
                ],
                ct).ConfigureAwait(false);

            if (ytdlp.ExitCode != 0 || !File.Exists(tmpPath))
            {
                ctx.Log.Out($"[WARN] yt-dlp deneme #{tried} başarısız.");
                if (GetFreeMb(dir) <= 0)
                {
                    ctx.Log.Out($"[HATA] Diskte yer kalmamış. Film atlanıyor: {name} ({year})");
                }
                TryDeleteFile(tmpPath);
                continue;
            }

            var sizeBytes = GetFileSize(tmpPath);
            if (sizeBytes < MinTrailerBytes)
            {
                ctx.Log.Out($"[WARN] Dosya çok küçük ({sizeBytes}B). Siliniyor ve sonraki aday denenecek...");
                TryDeleteFile(tmpPath);
                continue;
            }

            if (hasFfprobe)
            {
                var duration = await ProbeDurationAsync(tmpPath, ct).ConfigureAwait(false);
                if (duration > 0 && duration < MinTrailerDurationSeconds)
                {
                    ctx.Log.Out($"[WARN] Süre kısa ({duration.ToString("0.##", CultureInfo.InvariantCulture)}s). Siliniyor ve sonraki aday denenecek...");
                    TryDeleteFile(tmpPath);
                    continue;
                }
            }

            if (compareAfter && File.Exists(outFile))
            {
                var tmpDuration = hasFfprobe ? await ProbeDurationAsync(tmpPath, ct).ConfigureAwait(false) : 0d;
                var outDuration = hasFfprobe ? await ProbeDurationAsync(outFile, ct).ConfigureAwait(false) : 0d;
                var outSize = GetFileSize(outFile);

                if (IsBetterTrailer(sizeBytes, outSize, tmpDuration, outDuration))
                {
                    ctx.Log.Out("[OK] Yeni trailer daha iyi bulundu (if-better): değiştiriliyor.");
                    if (!TryMoveReplace(tmpPath, outFile))
                    {
                        ctx.Log.Err($"[HATA] mv başarısız, yazılamıyor: {outFile}");
                        TryDeleteFile(tmpPath);
                        return DownloadOutcome.Fail;
                    }

                    if (ctx.Options.EnableThemeLink == 1)
                    {
                        await EnsureBackdropsThemeAsync(dir, outFile, ctx.Log, ctx.Options.ThemeLinkMode, ct).ConfigureAwait(false);
                    }
                }
                else
                {
                    ctx.Log.Out("[ATLA] Mevcut trailer daha iyi/eşdeğer: yenisi silindi.");
                    TryDeleteFile(tmpPath);
                    if (ctx.Options.EnableThemeLink == 1)
                    {
                        await EnsureBackdropsThemeAsync(dir, outFile, ctx.Log, ctx.Options.ThemeLinkMode, ct).ConfigureAwait(false);
                    }
                    return DownloadOutcome.Skip;
                }
            }
            else
            {
                if (!TryMoveReplace(tmpPath, outFile))
                {
                    ctx.Log.Err($"[HATA] mv başarısız, yazılamıyor: {outFile}");
                    TryDeleteFile(tmpPath);
                    return DownloadOutcome.Fail;
                }

                if (ctx.Options.EnableThemeLink == 1)
                {
                    await EnsureBackdropsThemeAsync(dir, outFile, ctx.Log, ctx.Options.ThemeLinkMode, ct).ConfigureAwait(false);
                }
            }

            await RefreshItemAsync(
                ctx,
                itemId,
                "Recursive=true&ImageRefreshMode=Default&MetadataRefreshMode=Default&RegenerateTrickplay=false&ReplaceAllMetadata=false",
                ct).ConfigureAwait(false);

            ctx.Log.Out($"[OK] Eklendi ve yenilendi: {outFile}");
            if (ctx.SleepSecs > 0)
            {
                await Task.Delay(TimeSpan.FromSeconds(ctx.SleepSecs), ct).ConfigureAwait(false);
            }

            return DownloadOutcome.Ok;
        }

        ctx.Log.Out($"[ATLA] Uygun indirilebilir trailer bulunamadı: {name} ({year})");
        return DownloadOutcome.Fail;
    }

    private async Task<NfoOutcome> ProcessUrlNfoItemAsync(
        StepContext ctx,
        string? resolvedUserId,
        JellyfinItem item,
        string path,
        Dictionary<string, string?> seriesTmdbCache,
        Dictionary<string, JellyfinItem?> itemDetailsCache,
        Dictionary<string, string?> imdbMapCache,
        Dictionary<string, IReadOnlyList<TrailerCandidate>> trailerCache,
        CancellationToken ct)
    {
        var itemId = item.Id ?? string.Empty;
        var itemType = item.Type ?? string.Empty;
        var name = item.Name ?? "(adsiz)";
        var year = item.ProductionYear?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        var tmdb = GetProviderId(item.ProviderIds, "Tmdb", "MovieDb");
        var imdb = GetProviderId(item.ProviderIds, "Imdb");

        if (string.Equals(itemType, "Movie", StringComparison.OrdinalIgnoreCase))
        {
            var tmdbId = tmdb;
            if (string.IsNullOrWhiteSpace(tmdbId) && !string.IsNullOrWhiteSpace(imdb))
            {
                tmdbId = await ResolveMovieTmdbFromImdbAsync(ctx, imdb!, imdbMapCache, ct).ConfigureAwait(false);
            }

            if (string.IsNullOrWhiteSpace(tmdbId))
            {
                ctx.Log.Out($"[ATLA] TMDb ID yok: {name}");
                return NfoOutcome.NoTmdb;
            }

            var candidates = await GetMovieCandidatesAsync(ctx, tmdbId!, trailerCache, ct).ConfigureAwait(false);
            return await WriteFirstTrailerToNfoAsync(ctx, itemId, itemType, name, year, path, candidates, ct).ConfigureAwait(false);
        }

        if (itemType is "Episode" or "Season" or "Series")
        {
            var seriesContext = await ResolveSeriesContextAsync(
                ctx,
                resolvedUserId,
                item,
                itemType,
                tmdb,
                path,
                seriesTmdbCache,
                itemDetailsCache,
                ct).ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(seriesContext.SeriesTmdb))
            {
                ctx.Log.Out($"[ATLA] Series TMDb yok: {name}");
                return NfoOutcome.NoTmdb;
            }

            var candidates = await GetTvCandidatesAsync(
                ctx,
                seriesContext.SeriesTmdb!,
                seriesContext.SeasonNumber,
                seriesContext.EpisodeNumber,
                trailerCache,
                ct).ConfigureAwait(false);

            return await WriteFirstTrailerToNfoAsync(ctx, itemId, itemType, name, year, path, candidates, ct).ConfigureAwait(false);
        }

        ctx.Log.Out($"[ATLA] Tür desteklenmiyor: {itemType} - {name}");
        return NfoOutcome.Unsupported;
    }

    private async Task<NfoOutcome> WriteFirstTrailerToNfoAsync(
        StepContext ctx,
        string itemId,
        string itemType,
        string name,
        string year,
        string path,
        IReadOnlyList<TrailerCandidate> candidates,
        CancellationToken ct)
    {
        foreach (var candidate in candidates)
        {
            var url = BuildTrailerUrl(candidate);
            if (string.IsNullOrWhiteSpace(url))
            {
                continue;
            }

            var (nfoPath, root) = PickNfoPath(itemType, path);
            if (string.IsNullOrWhiteSpace(nfoPath) || string.IsNullOrWhiteSpace(root))
            {
                ctx.Log.Out($"[ATLA] NFO yolu çözülemedi: {name}");
                return NfoOutcome.Misc;
            }

            var writeStatus = await EnsureNfoTrailerAsync(nfoPath, root, url, ct).ConfigureAwait(false);
            switch (writeStatus)
            {
                case NfoWriteStatus.AlreadyHasTrailer:
                    return NfoOutcome.SkipHasTrailer;
                case NfoWriteStatus.WriteFailed:
                    return NfoOutcome.FailWrite;
                case NfoWriteStatus.Success:
                    var refreshOk = await RefreshItemAsync(
                        ctx,
                        itemId,
                        "Recursive=false&MetadataRefreshMode=FullRefresh&ImageRefreshMode=Default&ReplaceAllImages=false&ReplaceAllMetadata=false",
                        ct).ConfigureAwait(false);
                    if (!refreshOk)
                    {
                        ctx.Log.Out($"[WARN] Refresh çağrısı başarısız: {name}");
                        return NfoOutcome.FailRefresh;
                    }

                    ctx.Log.Out($"[OK] {name} -> {url}");
                    if (ctx.SleepSecs > 0)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(ctx.SleepSecs), ct).ConfigureAwait(false);
                    }
                    return NfoOutcome.Ok;
            }
        }

        ctx.Log.Out($"[ATLA] Trailer bulunamadı: {name}");
        return NfoOutcome.NotFound;
    }

    private sealed record SeriesContext(string? SeriesTmdb, int? SeasonNumber, int? EpisodeNumber);

    private async Task<SeriesContext> ResolveSeriesContextAsync(
        StepContext ctx,
        string? resolvedUserId,
        JellyfinItem item,
        string itemType,
        string? tmdb,
        string path,
        Dictionary<string, string?> seriesTmdbCache,
        Dictionary<string, JellyfinItem?> itemDetailsCache,
        CancellationToken ct)
    {
        if (itemType is not ("Series" or "Season" or "Episode"))
        {
            return new SeriesContext(null, null, null);
        }

        var itemId = item.Id ?? string.Empty;
        var details = await GetItemDetailsAsync(ctx, resolvedUserId, itemId, itemDetailsCache, ct).ConfigureAwait(false);
        var seriesId = details?.SeriesId;
        int? seasonNumber = null;
        int? episodeNumber = null;

        if (string.Equals(itemType, "Episode", StringComparison.OrdinalIgnoreCase))
        {
            seasonNumber = details?.ParentIndexNumber;
            episodeNumber = details?.IndexNumber;
        }
        else if (string.Equals(itemType, "Season", StringComparison.OrdinalIgnoreCase))
        {
            seasonNumber = details?.IndexNumber;
        }

        string? seriesTmdb = null;
        if (!string.IsNullOrWhiteSpace(seriesId))
        {
            if (!seriesTmdbCache.TryGetValue(seriesId, out seriesTmdb))
            {
                var seriesItem = await GetItemDetailsAsync(ctx, resolvedUserId, seriesId, itemDetailsCache, ct).ConfigureAwait(false);
                seriesTmdb = GetProviderId(seriesItem?.ProviderIds, "Tmdb", "MovieDb");
                seriesTmdbCache[seriesId] = seriesTmdb;
            }
        }

        if (string.IsNullOrWhiteSpace(seriesTmdb) && string.Equals(itemType, "Series", StringComparison.OrdinalIgnoreCase))
        {
            seriesTmdb = tmdb;
        }

        return new SeriesContext(seriesTmdb, seasonNumber, episodeNumber);
    }

    private async Task<IReadOnlyList<TrailerCandidate>> GetMovieCandidatesAsync(
        StepContext ctx,
        string tmdbId,
        Dictionary<string, IReadOnlyList<TrailerCandidate>> cache,
        CancellationToken ct)
    {
        var cacheKey = $"movie:{tmdbId}";
        if (cache.TryGetValue(cacheKey, out var cached))
        {
            return cached;
        }

        var baseQuery = $"api_key={Uri.EscapeDataString(ctx.Options.TmdbApiKey)}&language={Uri.EscapeDataString(ctx.Options.PreferredLang)}&include_video_language={Uri.EscapeDataString($"{ctx.PreferredIso639},{ctx.FallbackIso639},en,null")}";
        var wideQuery = $"api_key={Uri.EscapeDataString(ctx.Options.TmdbApiKey)}&language={Uri.EscapeDataString(ctx.Options.PreferredLang)}&include_video_language={Uri.EscapeDataString(DefaultIncludeLangsWide)}";

        var result = await GetFirstCandidateSetAsync(
            cacheKey,
            cache,
            [
                $"movie/{tmdbId}/videos?{baseQuery}",
                $"movie/{tmdbId}/videos?{wideQuery}"
            ],
            ct).ConfigureAwait(false);

        return result;
    }

    private async Task<IReadOnlyList<TrailerCandidate>> GetTvCandidatesAsync(
        StepContext ctx,
        string tvId,
        int? seasonNumber,
        int? episodeNumber,
        Dictionary<string, IReadOnlyList<TrailerCandidate>> cache,
        CancellationToken ct)
    {
        var cacheKey = $"tv:{tvId}:s{seasonNumber?.ToString(CultureInfo.InvariantCulture) ?? "-"}:e{episodeNumber?.ToString(CultureInfo.InvariantCulture) ?? "-"}";
        if (cache.TryGetValue(cacheKey, out var cached))
        {
            return cached;
        }

        var baseQuery = $"api_key={Uri.EscapeDataString(ctx.Options.TmdbApiKey)}&language={Uri.EscapeDataString(ctx.Options.PreferredLang)}&include_video_language={Uri.EscapeDataString($"{ctx.PreferredIso639},{ctx.FallbackIso639},en,null")}";
        var wideQuery = $"api_key={Uri.EscapeDataString(ctx.Options.TmdbApiKey)}&language={Uri.EscapeDataString(ctx.Options.PreferredLang)}&include_video_language={Uri.EscapeDataString(DefaultIncludeLangsWide)}";
        var routes = new List<string>();

        if (seasonNumber.HasValue && episodeNumber.HasValue)
        {
            routes.Add($"tv/{tvId}/season/{seasonNumber.Value}/episode/{episodeNumber.Value}/videos?{baseQuery}");
        }

        if (seasonNumber.HasValue)
        {
            routes.Add($"tv/{tvId}/season/{seasonNumber.Value}/videos?{baseQuery}");
        }

        routes.Add($"tv/{tvId}/videos?{baseQuery}");

        if (seasonNumber.HasValue && episodeNumber.HasValue)
        {
            routes.Add($"tv/{tvId}/season/{seasonNumber.Value}/episode/{episodeNumber.Value}/videos?{wideQuery}");
        }

        if (seasonNumber.HasValue)
        {
            routes.Add($"tv/{tvId}/season/{seasonNumber.Value}/videos?{wideQuery}");
        }

        routes.Add($"tv/{tvId}/videos?{wideQuery}");

        return await GetFirstCandidateSetAsync(cacheKey, cache, routes, ct).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<TrailerCandidate>> GetFirstCandidateSetAsync(
        string cacheKey,
        Dictionary<string, IReadOnlyList<TrailerCandidate>> cache,
        IEnumerable<string> routes,
        CancellationToken ct)
    {
        foreach (var route in routes)
        {
            var resp = await GetTmdbJsonAsync<TmdbVideosResponse>(route, ct).ConfigureAwait(false);
            var candidates = ExtractCandidates(resp);
            if (candidates.Count > 0)
            {
                cache[cacheKey] = candidates;
                return candidates;
            }
        }

        cache[cacheKey] = Array.Empty<TrailerCandidate>();
        return cache[cacheKey];
    }

    private async Task<string?> ResolveMovieTmdbFromImdbAsync(
        StepContext ctx,
        string imdbId,
        Dictionary<string, string?> cache,
        CancellationToken ct)
    {
        if (cache.TryGetValue(imdbId, out var cached))
        {
            return cached;
        }

        var route = $"find/{Uri.EscapeDataString(imdbId)}?api_key={Uri.EscapeDataString(ctx.Options.TmdbApiKey)}&external_source=imdb_id";
        var resp = await GetTmdbJsonAsync<TmdbFindResponse>(route, ct).ConfigureAwait(false);
        var tmdbId = resp?.MovieResults?.FirstOrDefault()?.Id.ToString(CultureInfo.InvariantCulture);
        cache[imdbId] = tmdbId;
        return tmdbId;
    }

    private async Task<string?> ResolveUserIdAsync(StepContext ctx, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(ctx.Options.JfUserId))
        {
            return ctx.Options.JfUserId;
        }

        var users = await GetJellyfinJsonAsync<List<JellyfinUser>>(ctx, "Users", ct).ConfigureAwait(false) ?? [];
        var picked = users.FirstOrDefault(u => u.Policy?.IsAdministrator == true)?.Id
                     ?? users.FirstOrDefault()?.Id;

        if (string.IsNullOrWhiteSpace(picked))
        {
            throw new InvalidOperationException("Kullanıcı bulunamadı.");
        }

        return picked;
    }

    private async Task<JellyfinItemsResponse> GetItemsPageAsync(
        StepContext ctx,
        string? resolvedUserId,
        bool userScoped,
        string fields,
        int start,
        int limit,
        CancellationToken ct)
    {
        var scopePrefix = userScoped && !string.IsNullOrWhiteSpace(resolvedUserId)
            ? $"Users/{Uri.EscapeDataString(resolvedUserId!)}/Items"
            : "Items";

        var route = $"{scopePrefix}?IncludeItemTypes={Uri.EscapeDataString(ctx.IncludeTypes)}&Recursive=true&Fields={Uri.EscapeDataString(fields)}&StartIndex={start}&Limit={limit}";
        return await GetJellyfinJsonAsync<JellyfinItemsResponse>(ctx, route, ct).ConfigureAwait(false)
               ?? new JellyfinItemsResponse(0, []);
    }

    private async Task<JellyfinItem?> GetItemDetailsAsync(
        StepContext ctx,
        string? resolvedUserId,
        string itemId,
        Dictionary<string, JellyfinItem?> cache,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(itemId))
        {
            return null;
        }

        if (cache.TryGetValue(itemId, out var cached))
        {
            return cached;
        }

        var scopePrefix = !string.IsNullOrWhiteSpace(resolvedUserId)
            ? $"Users/{Uri.EscapeDataString(resolvedUserId!)}/Items/{Uri.EscapeDataString(itemId)}"
            : $"Items/{Uri.EscapeDataString(itemId)}";

        var route = $"{scopePrefix}?Fields=SeriesId,SeasonId,IndexNumber,ParentIndexNumber,ProviderIds,Type,Path,MediaSources";
        var item = await GetJellyfinJsonAsync<JellyfinItem>(ctx, route, ct).ConfigureAwait(false);
        cache[itemId] = item;
        return item;
    }

    private async Task<bool> RefreshItemAsync(StepContext ctx, string itemId, string query, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(itemId))
        {
            return false;
        }

        var route = $"Items/{Uri.EscapeDataString(itemId)}/Refresh?{query}";
        using var req = CreateJellyfinRequest(ctx, HttpMethod.Post, route);
        using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
        return resp.IsSuccessStatusCode;
    }

    private async Task<T?> GetJellyfinJsonAsync<T>(StepContext ctx, string route, CancellationToken ct)
    {
        using var req = CreateJellyfinRequest(ctx, HttpMethod.Get, route);
        using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, ct).ConfigureAwait(false);
    }

    private async Task<T?> GetTmdbJsonAsync<T>(string route, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, BuildTmdbUri(route));
        using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            return default;
        }

        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, ct).ConfigureAwait(false);
    }

    private static HttpRequestMessage CreateJellyfinRequest(StepContext ctx, HttpMethod method, string route)
    {
        var req = new HttpRequestMessage(method, BuildJellyfinUri(ctx.Options.JfBase, route));
        req.Headers.TryAddWithoutValidation("X-Emby-Token", ctx.Options.JfApiKey);
        return req;
    }

    private static Uri BuildJellyfinUri(string baseUrl, string route)
    {
        var normalized = string.IsNullOrWhiteSpace(baseUrl) ? "http://localhost:8096/" : baseUrl.Trim();
        if (!normalized.EndsWith("/", StringComparison.Ordinal))
        {
            normalized += "/";
        }

        return new Uri(new Uri(normalized, UriKind.Absolute), route.TrimStart('/'));
    }

    private static Uri BuildTmdbUri(string route)
    {
        return new Uri(new Uri("https://api.themoviedb.org/3/", UriKind.Absolute), route.TrimStart('/'));
    }

    private static bool ValidateCommonConfig(StepContext ctx, bool requireTmdb, out string error)
    {
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(ctx.Options.JfApiKey) || string.Equals(ctx.Options.JfApiKey, "CHANGE_ME", StringComparison.OrdinalIgnoreCase))
        {
            error = "Hata: JF_API_KEY ve TMDB_API_KEY ayarla.";
            return false;
        }

        if (requireTmdb && (string.IsNullOrWhiteSpace(ctx.Options.TmdbApiKey) || string.Equals(ctx.Options.TmdbApiKey, "CHANGE_ME", StringComparison.OrdinalIgnoreCase)))
        {
            error = "Hata: JF_API_KEY ve TMDB_API_KEY ayarla.";
            return false;
        }

        _ = BuildJellyfinUri(ctx.Options.JfBase, "/");
        return true;
    }

    private static TrailerRunOptions NormalizeOptions(TrailerRunOptions options)
    {
        return new TrailerRunOptions
        {
            JfBase = string.IsNullOrWhiteSpace(options.JfBase) ? "http://localhost:8096" : options.JfBase.Trim(),
            JfApiKey = options.JfApiKey?.Trim() ?? string.Empty,
            TmdbApiKey = options.TmdbApiKey?.Trim() ?? string.Empty,
            PreferredLang = string.IsNullOrWhiteSpace(options.PreferredLang) ? DefaultPreferredLang : options.PreferredLang.Trim(),
            FallbackLang = string.IsNullOrWhiteSpace(options.FallbackLang) ? DefaultFallbackLang : options.FallbackLang.Trim(),
            IncludeTypes = string.IsNullOrWhiteSpace(options.IncludeTypes) ? DefaultIncludeTypes : options.IncludeTypes.Trim(),
            PageSize = options.PageSize > 0 ? options.PageSize : DefaultPageSize,
            SleepSecs = options.SleepSecs >= 0 ? options.SleepSecs : DefaultSleepSecs,
            JfUserId = string.IsNullOrWhiteSpace(options.JfUserId) ? null : options.JfUserId.Trim(),
            OverwritePolicy = string.IsNullOrWhiteSpace(options.OverwritePolicy) ? "skip" : options.OverwritePolicy.Trim(),
            EnableThemeLink = options.EnableThemeLink,
            ThemeLinkMode = string.IsNullOrWhiteSpace(options.ThemeLinkMode) ? "symlink" : options.ThemeLinkMode.Trim().ToLowerInvariant()
        };
    }

    private static bool TryNormalizeOverwritePolicy(string? value, out string normalized)
    {
        normalized = (value ?? "skip").Trim().ToLowerInvariant();
        if (normalized is "skip" or "replace" or "if-better")
        {
            return true;
        }

        normalized = string.Empty;
        return false;
    }

    private static string ResolveItemDirectory(string path, string itemType)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return path;
        }

        if (itemType is "Series" or "Season")
        {
            if (!Path.HasExtension(path) || Directory.Exists(path))
            {
                return path;
            }
        }

        if (!Path.HasExtension(path) && Directory.Exists(path))
        {
            return path;
        }

        return Path.GetDirectoryName(path) ?? path;
    }

    private static IReadOnlyList<TrailerCandidate> ExtractCandidates(TmdbVideosResponse? response)
    {
        if (response?.Results == null || response.Results.Count == 0)
        {
            return Array.Empty<TrailerCandidate>();
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ordered = response.Results
            .Where(v => v.Site != null && (v.Site.Equals("YouTube", StringComparison.OrdinalIgnoreCase) || v.Site.Equals("Vimeo", StringComparison.OrdinalIgnoreCase)))
            .OrderBy(v => string.Equals(v.Type, "Trailer", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(v => 0);

        var list = new List<TrailerCandidate>();
        foreach (var video in ordered)
        {
            var site = video.Site!.ToLowerInvariant();
            var key = site switch
            {
                "youtube" => NormalizeYoutubeKey(video.Key),
                "vimeo" => NormalizeVimeoKey(video.Key),
                _ => null
            };

            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            var dedupeKey = $"{site}|{key}";
            if (!seen.Add(dedupeKey))
            {
                continue;
            }

            if (!string.Equals(video.Type, "Trailer", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(video.Type, "Teaser", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            list.Add(new TrailerCandidate(site, key));
        }

        return list;
    }

    private static string? NormalizeYoutubeKey(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var trimmed = raw.Trim();
        if (Regex.IsMatch(trimmed, "^[A-Za-z0-9_-]{11}$"))
        {
            return trimmed;
        }

        var vMatch = Regex.Match(trimmed, @"[\?&]v=([A-Za-z0-9_-]{11})");
        if (vMatch.Success)
        {
            return vMatch.Groups[1].Value;
        }

        var shortMatch = Regex.Match(trimmed, @"youtu\.be/([A-Za-z0-9_-]{11})");
        return shortMatch.Success ? shortMatch.Groups[1].Value : null;
    }

    private static string? NormalizeVimeoKey(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var trimmed = raw.Trim();
        if (Regex.IsMatch(trimmed, @"^[0-9]+$"))
        {
            return trimmed;
        }

        var match = Regex.Match(trimmed, @"([0-9]{6,})");
        return match.Success ? match.Groups[1].Value : null;
    }

    private static string BuildTrailerUrl(TrailerCandidate candidate)
    {
        return candidate.Site switch
        {
            "youtube" => $"plugin://plugin.video.youtube/?action=play_video&videoid={candidate.Key}",
            "vimeo" => $"https://vimeo.com/{candidate.Key}",
            _ => string.Empty
        };
    }

    private static string? GetProviderId(Dictionary<string, string>? providerIds, params string[] keys)
    {
        if (providerIds == null || providerIds.Count == 0)
        {
            return null;
        }

        foreach (var key in keys)
        {
            if (providerIds.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                return value;
            }

            var pair = providerIds.FirstOrDefault(kv => string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(pair.Value))
            {
                return pair.Value;
            }
        }

        return null;
    }

    private static string GetIso639(string? lang)
    {
        if (string.IsNullOrWhiteSpace(lang))
        {
            return "en";
        }

        var idx = lang.IndexOf('-', StringComparison.Ordinal);
        return idx > 0 ? lang[..idx] : lang;
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        return values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? string.Empty;
    }

    private static bool TryEnsureDirectory(string path, out string? error)
    {
        error = null;
        try
        {
            Directory.CreateDirectory(path);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static bool CheckDirectoryWritable(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            var probe = Path.Combine(path, $".jmsf_probe_{Environment.ProcessId}_{Guid.NewGuid():N}");
            using (File.Create(probe))
            {
            }
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static long GetFreeMb(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var root = Path.GetPathRoot(full);
            if (string.IsNullOrWhiteSpace(root))
            {
                return 0;
            }

            var drive = new DriveInfo(root);
            return drive.AvailableFreeSpace / (1024 * 1024);
        }
        catch
        {
            return 0;
        }
    }

    private static long GetFileSize(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch
        {
            return 0;
        }
    }

    private static async Task<double> ProbeDurationAsync(string path, CancellationToken ct)
    {
        if (!CommandExists("ffprobe"))
        {
            return 0d;
        }

        var result = await RunProcessAsync(
            "ffprobe",
            [
                "-v", "error",
                "-show_entries", "format=duration",
                "-of", "default=nw=1:nk=1",
                path
            ],
            ct).ConfigureAwait(false);

        if (result.ExitCode != 0)
        {
            return 0d;
        }

        var line = result.Stdout
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault();

        return double.TryParse(line, NumberStyles.Float, CultureInfo.InvariantCulture, out var duration)
            ? duration
            : 0d;
    }

    private static bool IsBetterTrailer(long newSize, long oldSize, double newDuration, double oldDuration)
    {
        if (newDuration > 0 && oldDuration > 0 && (newDuration - oldDuration) >= BetterMinDurationDelta)
        {
            return true;
        }

        return (newSize - oldSize) >= BetterMinSizeDelta;
    }

    private static bool TryMoveReplace(string source, string destination)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination) ?? destination);
            if (File.Exists(destination))
            {
                File.Delete(destination);
            }

            File.Move(source, destination);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void TryDeleteFile(string? path)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    private static void CleanupTemporaryFiles(IEnumerable<string> seenDirs, string workDir)
    {
        foreach (var dir in seenDirs.Where(Directory.Exists))
        {
            CleanupTemporaryFilesInDirectory(dir, recurse: false);
        }

        if (Directory.Exists(workDir))
        {
            CleanupTemporaryFilesInDirectory(workDir, recurse: true);
        }
    }

    private static void CleanupTemporaryFilesInDirectory(string root, bool recurse)
    {
        var option = recurse ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
        try
        {
            foreach (var file in Directory.EnumerateFiles(root, "*", option))
            {
                var name = Path.GetFileName(file);
                if (name.EndsWith(".part", StringComparison.OrdinalIgnoreCase) ||
                    name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) ||
                    name.EndsWith(".tmp.mp4", StringComparison.OrdinalIgnoreCase) ||
                    name.EndsWith(".ytdl", StringComparison.OrdinalIgnoreCase))
                {
                    TryDeleteFile(file);
                }
            }
        }
        catch
        {
        }
    }

    private static async Task EnsureBackdropsThemeAsync(
        string dir,
        string trailerPath,
        StepLogger log,
        string mode,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var backdropsDir = Path.Combine(dir, "backdrops");
        var themePath = Path.Combine(backdropsDir, "theme.mp4");

        try
        {
            Directory.CreateDirectory(backdropsDir);
        }
        catch
        {
            log.Out($"[WARN] backdrops klasörü oluşturulamadı: {backdropsDir}");
            return;
        }

        if (File.Exists(themePath))
        {
            return;
        }

        var relativeTarget = Path.GetRelativePath(backdropsDir, trailerPath);
        var normalizedMode = string.IsNullOrWhiteSpace(mode) ? "symlink" : mode.Trim().ToLowerInvariant();

        switch (normalizedMode)
        {
            case "symlink":
                if (TryCreateSymbolicLink(themePath, relativeTarget) || TryCreateSymbolicLink(themePath, trailerPath))
                {
                    log.Out($"[OK] theme.mp4 için symlink oluşturuldu (mode=symlink): {themePath} -> {trailerPath}");
                }
                else if (TryCreateHardLink(themePath, trailerPath))
                {
                    log.Out($"[OK] symlink mümkün değil, hardlink fallback kullanıldı (mode=symlink): {themePath}");
                }
                else
                {
                    log.Out("[WARN] Symlink/hardlink oluşturulamadı, theme.mp4 atlanıyor (mode=symlink).");
                    return;
                }
                break;
            case "hardlink":
                if (TryCreateHardLink(themePath, trailerPath))
                {
                    log.Out($"[OK] theme.mp4 için hardlink oluşturuldu (mode=hardlink): {themePath}");
                }
                else if (TryCreateSymbolicLink(themePath, relativeTarget) || TryCreateSymbolicLink(themePath, trailerPath))
                {
                    log.Out($"[OK] hardlink mümkün değil, symlink fallback kullanıldı (mode=hardlink): {themePath}");
                }
                else
                {
                    log.Out("[WARN] Hardlink/symlink oluşturulamadı, theme.mp4 atlanıyor (mode=hardlink).");
                    return;
                }
                break;
            default:
                try
                {
                    File.Copy(trailerPath, themePath, overwrite: true);
                    log.Out($"[OK] theme.mp4 kopyalandı (mode=copy): {themePath}");
                }
                catch
                {
                    log.Out($"[WARN] copy mode: theme.mp4 kopyalanamadı: {themePath}");
                    return;
                }
                break;
        }

        log.Out($"[OK] backdrops/theme.mp4 hazırlandı → {themePath}");
    }

    private static bool TryCreateSymbolicLink(string linkPath, string targetPath)
    {
        try
        {
            if (File.Exists(linkPath))
            {
                File.Delete(linkPath);
            }

            File.CreateSymbolicLink(linkPath, targetPath);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryCreateHardLink(string linkPath, string existingFilePath)
    {
        try
        {
            if (File.Exists(linkPath))
            {
                File.Delete(linkPath);
            }

            var psi = new ProcessStartInfo
            {
                FileName = "ln",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            psi.ArgumentList.Add(existingFilePath);
            psi.ArgumentList.Add(linkPath);

            using var process = Process.Start(psi);
            process?.WaitForExit(3000);
            return process is { ExitCode: 0 };
        }
        catch
        {
            return false;
        }
    }

    private enum NfoWriteStatus
    {
        Success,
        AlreadyHasTrailer,
        WriteFailed
    }

    private static async Task<NfoWriteStatus> EnsureNfoTrailerAsync(string nfoPath, string root, string trailerUrl, CancellationToken ct)
    {
        try
        {
            if (File.Exists(nfoPath))
            {
                var xml = await File.ReadAllTextAsync(nfoPath, ct).ConfigureAwait(false);
                if (Regex.IsMatch(xml, @"<trailer>.*?</trailer>", RegexOptions.IgnoreCase | RegexOptions.Singleline))
                {
                    return NfoWriteStatus.AlreadyHasTrailer;
                }

                var escapedUrl = SecurityElement.Escape(trailerUrl) ?? trailerUrl;
                var regex = new Regex($"</{Regex.Escape(root)}>", RegexOptions.IgnoreCase | RegexOptions.RightToLeft);
                var replaced = regex.Replace(xml, $"  <trailer>{escapedUrl}</trailer>{Environment.NewLine}</{root}>", 1);
                if (ReferenceEquals(replaced, xml) || string.Equals(replaced, xml, StringComparison.Ordinal))
                {
                    return NfoWriteStatus.WriteFailed;
                }

                await File.WriteAllTextAsync(nfoPath, replaced, ct).ConfigureAwait(false);
                return NfoWriteStatus.Success;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(nfoPath) ?? ".");
            var escaped = SecurityElement.Escape(trailerUrl) ?? trailerUrl;
            var xmlDoc = $@"<?xml version=""1.0"" encoding=""utf-8""?>
<{root}>
  <trailer>{escaped}</trailer>
</{root}>
";
            await File.WriteAllTextAsync(nfoPath, xmlDoc, ct).ConfigureAwait(false);
            return NfoWriteStatus.Success;
        }
        catch
        {
            return NfoWriteStatus.WriteFailed;
        }
    }

    private static (string nfoPath, string root) PickNfoPath(string itemType, string path)
    {
        return itemType switch
        {
            "Movie" => PickMovieNfoPath(path),
            "Episode" => ($"{Path.ChangeExtension(path, null)}.nfo", "episodedetails"),
            "Series" => (Path.Combine(path, "tvshow.nfo"), "tvshow"),
            "Season" => (Path.Combine(path, "season.nfo"), "season"),
            _ => (string.Empty, string.Empty)
        };
    }

    private static (string nfoPath, string root) PickMovieNfoPath(string path)
    {
        var dir = Path.GetDirectoryName(path) ?? ".";
        var name = Path.GetFileNameWithoutExtension(path);
        var candidate = Path.Combine(dir, $"{name}.nfo");
        if (File.Exists(candidate))
        {
            return (candidate, "movie");
        }

        var movieNfo = Path.Combine(dir, "movie.nfo");
        if (File.Exists(movieNfo))
        {
            return (movieNfo, "movie");
        }

        return (candidate, "movie");
    }

    private static string SanitizeFileName(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return Guid.NewGuid().ToString("N");
        }

        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(input.Length);
        foreach (var ch in input)
        {
            sb.Append(invalid.Contains(ch) ? '_' : ch);
        }

        return sb.ToString();
    }

    private static string? ResolveYtDlpCommand()
    {
        foreach (var candidate in GetBundledYtDlpCandidates())
        {
            var staged = StageExecutableFromFile(candidate, "yt-dlp");
            if (!string.IsNullOrWhiteSpace(staged))
            {
                return staged;
            }
        }

        var stagedResource = StageExecutableFromResource(
            ["Resources.slider.yt-dlp.yt-dlp", ".slider.yt-dlp.yt-dlp"],
            "yt-dlp");
        if (!string.IsNullOrWhiteSpace(stagedResource))
        {
            return stagedResource;
        }

        return CommandExists("yt-dlp") ? "yt-dlp" : null;
    }

    private static IEnumerable<string> GetBundledYtDlpCandidates()
    {
        var assemblyDir = Path.GetDirectoryName(typeof(TrailerAutomationService).Assembly.Location) ?? string.Empty;
        var currentDir = Directory.GetCurrentDirectory();

        return new[]
        {
            Path.Combine(currentDir, "Resources", "slider", "yt-dlp", "yt-dlp"),
            Path.Combine(assemblyDir, "Resources", "slider", "yt-dlp", "yt-dlp"),
            Path.Combine(AppContext.BaseDirectory, "Resources", "slider", "yt-dlp", "yt-dlp")
        }.Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static string? StageExecutableFromFile(string sourcePath, string fileName)
    {
        try
        {
            if (!File.Exists(sourcePath))
            {
                return null;
            }

            var targetDir = Path.Combine(Path.GetTempPath(), DefaultToolDirName);
            Directory.CreateDirectory(targetDir);
            var targetPath = Path.Combine(targetDir, fileName);
            File.Copy(sourcePath, targetPath, overwrite: true);
            EnsureExecutable(targetPath);
            return targetPath;
        }
        catch
        {
            return null;
        }
    }

    private static string? StageExecutableFromResource(IEnumerable<string> suffixCandidates, string fileName)
    {
        try
        {
            var assembly = typeof(TrailerAutomationService).Assembly;
            var resourceName = assembly.GetManifestResourceNames()
                .FirstOrDefault(name => suffixCandidates.Any(suffix => name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)));

            if (string.IsNullOrWhiteSpace(resourceName))
            {
                return null;
            }

            var targetDir = Path.Combine(Path.GetTempPath(), DefaultToolDirName);
            Directory.CreateDirectory(targetDir);
            var targetPath = Path.Combine(targetDir, fileName);

            using var source = assembly.GetManifestResourceStream(resourceName);
            if (source == null)
            {
                return null;
            }

            using (var destination = File.Create(targetPath))
            {
                source.CopyTo(destination);
            }

            EnsureExecutable(targetPath);
            return targetPath;
        }
        catch
        {
            return null;
        }
    }

    private static void EnsureExecutable(string path)
    {
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(
                    path,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                    UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }
        }
        catch
        {
        }
    }

    private static bool CommandExists(string commandName)
    {
        if (string.IsNullOrWhiteSpace(commandName))
        {
            return false;
        }

        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathEnv))
        {
            return false;
        }

        var names = OperatingSystem.IsWindows()
            ? new[] { commandName, $"{commandName}.exe", $"{commandName}.cmd", $"{commandName}.bat" }
            : new[] { commandName };

        foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var name in names)
            {
                try
                {
                    var fullPath = Path.Combine(dir, name);
                    if (File.Exists(fullPath))
                    {
                        return true;
                    }
                }
                catch
                {
                }
            }
        }

        return false;
    }

    private static async Task<ProcessRunResult> RunProcessAsync(string fileName, IEnumerable<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using var process = new Process
        {
            StartInfo = psi,
            EnableRaisingEvents = true
        };

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data != null)
            {
                stdout.AppendLine(e.Data);
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null)
            {
                stderr.AppendLine(e.Data);
            }
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var reg = ct.Register(() =>
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
            }
        });

        await process.WaitForExitAsync(ct).ConfigureAwait(false);
        return new ProcessRunResult(process.ExitCode, stdout.ToString(), stderr.ToString());
    }
}
