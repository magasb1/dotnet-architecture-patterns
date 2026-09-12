using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FlyleafLib.MediaPlayer;
using Microsoft.Win32;
using StorageDemo.Core.Documents;
using StorageDemo.Grpc;

// Aliased because the generated namespace is StorageDemo.Grpc, which hides the transport's own
// Grpc.Core from anything written below, and because FlyleafLib already owns the name Status here.
using RpcException = Grpc.Core.RpcException;
using RpcStatusCode = Grpc.Core.StatusCode;

namespace StorageDemo.Client;

public partial class MainWindow : Window
{
    /// <summary>Text files are previewed, not opened wholesale; a huge log would freeze the UI.</summary>
    private const int TextPreviewLimit = 512 * 1024;

    private static readonly TimeSpan SkipStep = TimeSpan.FromSeconds(10);

    private readonly ObservableCollection<DocumentItem> _documents = [];
    private readonly ObservableCollection<DocumentItem> _streams = [];
    private readonly ServerList _servers = ServerList.Load();
    private readonly CancellationTokenSource _closing = new();

    /// <summary>Drives the position readout. Polling beats binding here: one place to suspend
    /// while the user is dragging the scrub bar.</summary>
    private readonly DispatcherTimer _positionTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };

    /// <summary>
    /// Live sessions are polled rather than pushed. They are few, they change constantly while
    /// running, and a stream that ends has nothing to announce on the document change feed.
    /// </summary>
    private readonly DispatcherTimer _liveTimer = new() { Interval = TimeSpan.FromSeconds(2) };

    /// <summary>
    /// The sensor set for the one stream being watched. It runs only while a stream with KLV is
    /// playing: it is a per-watched-stream fetch and nothing about it belongs on a grid of tiles.
    /// </summary>
    private readonly DispatcherTimer _klvTimer = new() { Interval = TimeSpan.FromSeconds(1) };

    private readonly ICollectionView _view;

    /// <summary>The SRT address the server hands out for its consumption port, when it knows one.</summary>
    private string _consumptionUrl = string.Empty;

    /// <summary>Where the server's REST surface is, when it knows. Empty means download instead.</summary>
    private string _contentBaseUrl = string.Empty;

    /// <summary>
    /// Above this, a video is streamed and seeked rather than downloaded first.
    ///
    /// A camera recording is hours long and gigabytes big; waiting for all of it before the first
    /// frame is not watching it. Below this a download is the better trade, because it is quick
    /// and every later view of the same file is instant.
    /// </summary>
    private const long StreamRatherThanDownloadBytes = 64L * 1024 * 1024;

    /// <summary>False until the constructor has finished wiring everything up.</summary>
    private readonly bool _ready;

    private DocumentsApi? _api;
    private Player? _player;
    private bool _isScrubbing;
    private bool _suppressSeek;

    /// <summary>Cancels the watch loop and any in-flight call belonging to the previous server.</summary>
    private CancellationTokenSource _connection = new();

    public MainWindow(string initialAddress)
    {
        InitializeComponent();

        DocumentList.ItemsSource = _documents;
        StreamList.ItemsSource = _streams;

        // WPF wraps any bound collection in a view; filtering through it leaves the collection
        // alone, so a hidden tile keeps its thumbnail and comes straight back when the filter
        // clears. Only documents are filtered: a stream is either running or gone.
        _view = CollectionViewSource.GetDefaultView(_documents);
        _view.Filter = MatchesFilters;
        ServerBox.ItemsSource = _servers.Entries;
        ServerBox.Text = initialAddress;
        TokenBox.Text = _servers.TokenFor(initialAddress);

        _positionTimer.Tick += OnPositionTick;
        _liveTimer.Tick += async (_, _) => await RefreshLiveAsync();
        _klvTimer.Tick += async (_, _) => await RefreshKlvAsync();

        PreviewKeyDown += OnShortcut;

        // Last, so anything that fires while the window is being built sees a half-built window
        // and stands down rather than reaching for a control that is not there.
        _ready = true;

        Loaded += async (_, _) => await ConnectAsync(initialAddress);
        Closed += (_, _) =>
        {
            _closing.Cancel();
            _connection.Cancel();
            _positionTimer.Stop();
            _liveTimer.Stop();
            _klvTimer.Stop();
            _player?.Dispose();
            _api?.Dispose();
        };
    }

    /// <summary>
    /// Switches the client to another server. Nothing below the transport changes, which is the
    /// point of the demo: one client against any storage and database combination.
    /// </summary>
    private async Task ConnectAsync(string address)
    {
        address = address.Trim();

        // A list entry renders as "Name  -  http://host"; recover the address behind the label.
        var selected = _servers.Entries.FirstOrDefault(entry => entry.ToString() == address);
        if (selected is not null)
        {
            address = selected.Address;
        }

        // What is in the box wins over what was saved, so a token can be corrected by typing it.
        var token = TokenBox.Text.Trim();

        if (string.IsNullOrWhiteSpace(address))
        {
            return;
        }

        await _connection.CancelAsync();
        _connection.Dispose();
        _connection = CancellationTokenSource.CreateLinkedTokenSource(_closing.Token);

        _liveTimer.Stop();
        StopKlv();
        _api?.Dispose();
        _documents.Clear();
        _streams.Clear();
        NoStreamsHint.Visibility = Visibility.Visible;
        HideAllPreviews();
        PreviewTitle.Text = "Select a document";

        try
        {
            _api = new DocumentsApi(address, token);
        }
        catch (Exception ex)
        {
            SetStatus($"{address} is not a usable address: {ex.Message}");
            return;
        }

        SetStatus($"Connecting to {address}...");

        try
        {
            var providers = await _api.GetProvidersAsync(_connection.Token);
            _contentBaseUrl = providers.ContentBaseUrl;
            ProviderText.Text = $"{address}   storage: {providers.Storage}   database: {providers.Database}";
            _servers.Remember(address, token);
        }
        catch (Exception ex)
        {
            ProviderText.Text = address;
            SetStatus($"Cannot reach {address}: {ex.Message}");
            return;
        }

        await RefreshAsync();
        await RefreshLiveAsync();

        _liveTimer.Start();

        // Fire and forget: the watch loop lives until the next connect or the window closes.
        _ = WatchAsync(_api, _connection.Token);
    }

    /// <summary>
    /// Whatever is selected, in either tab. Selecting in one list clears the other, so at most one
    /// of them ever has a selection and the preview always has a single subject.
    /// </summary>
    private DocumentItem? Selected =>
        (StreamList.SelectedItem ?? DocumentList.SelectedItem) as DocumentItem;

    /// <summary>Name search plus type and age, all evaluated against the tile already in memory.</summary>
    private bool MatchesFilters(object candidate)
    {
        // Same reason as OnFilterChanged: during construction the filter controls do not exist.
        if (!_ready)
        {
            return true;
        }

        if (candidate is not DocumentItem item)
        {
            return false;
        }

        var search = SearchBox.Text.Trim();
        if (search.Length > 0
            && !item.FileName.Contains(search, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (SelectedLabel(TypeFilter) is { } type && type != "All types" && KindLabel(item.Kind) != type)
        {
            return false;
        }

        return SelectedLabel(DateFilter) switch
        {
            "Today" => item.CreatedAt.Date == DateTimeOffset.Now.Date,
            "Last 7 days" => item.CreatedAt > DateTimeOffset.Now.AddDays(-7),
            "Last 30 days" => item.CreatedAt > DateTimeOffset.Now.AddDays(-30),
            _ => true,
        };
    }

    private static string KindLabel(DocumentKind kind) => kind switch
    {
        DocumentKind.Image => "Images",
        DocumentKind.Video => "Video",
        DocumentKind.Audio => "Audio",
        DocumentKind.Text => "Text",
        DocumentKind.Pdf => "PDF",
        _ => "Other",
    };

    private static string? SelectedLabel(Selector box)
        => (box.SelectedItem as ComboBoxItem)?.Content as string;

    private void OnFilterChanged(object sender, RoutedEventArgs e)
    {
        // A ComboBox with SelectedIndex set in XAML raises SelectionChanged while the window is
        // still being built, before the collection view exists and before the other controls this
        // handler reads have been created. There is nothing to filter yet either way.
        if (!_ready)
        {
            return;
        }

        _view.Refresh();
        UpdateFilterStatus();
    }

    /// <summary>Switching tabs drops the other tab's selection, so the preview follows the tab.</summary>
    private void OnTabChanged(object sender, SelectionChangedEventArgs e)
    {
        // TabControl selection bubbles the inner lists' events too; only the tabs matter here.
        if (!_ready || !ReferenceEquals(e.OriginalSource, SidebarTabs))
        {
            return;
        }

        StreamList.SelectedItem = null;
        DocumentList.SelectedItem = null;
    }

    private void OnClearFilters(object sender, RoutedEventArgs e)
    {
        SearchBox.Clear();
        TypeFilter.SelectedIndex = 0;
        DateFilter.SelectedIndex = 0;
    }

    private void UpdateFilterStatus()
    {
        var shown = _view.Cast<DocumentItem>().Count();

        SetStatus(shown == _documents.Count
            ? $"{_documents.Count} document(s)."
            : $"{shown} of {_documents.Count} document(s) match.");
    }

    private async void OnConnect(object sender, RoutedEventArgs e) => await ConnectAsync(ServerBox.Text);

    /// <summary>Picking a saved server brings its token with it, so it is typed once.</summary>
    private void OnServerSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_ready && ServerBox.SelectedItem is ServerEntry entry)
        {
            TokenBox.Text = entry.Token;
        }
    }

    private async void OnServerKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            await ConnectAsync(ServerBox.Text);
        }
    }

    /// <summary>
    /// Applies changes the server pushes, including ones its storage monitor found on disk or in
    /// the bucket. Each event touches a single tile: reloading the whole list would throw away
    /// every thumbnail and make the grid flicker each time an analysis finishes.
    /// </summary>
    private async Task WatchAsync(DocumentsApi api, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await foreach (var change in api.WatchAsync(cancellationToken))
                {
                    await ApplyChangeAsync(api, change, cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                SetStatus($"Change feed dropped: {ex.Message}");
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            // The stream ends when the server restarts; reconnect rather than going stale.
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task ApplyChangeAsync(
        DocumentsApi api,
        ChangeEvent change,
        CancellationToken cancellationToken)
    {
        var existing = _documents.FirstOrDefault(item => item.Id == change.DocumentId);

        if (change.Kind == ChangeEvent.Types.Kind.Removed)
        {
            if (existing is not null)
            {
                _documents.Remove(existing);
            }

            SetStatus($"Removed {change.FileName}");
            return;
        }

        var document = await api.GetAsync(change.DocumentId, cancellationToken);
        if (document is null)
        {
            return;
        }

        if (existing is null)
        {
            // Uploaded by another client, or found in the store by the monitor.
            var item = new DocumentItem(document);
            InsertSorted(item);
            _ = LoadThumbnailAsync(item);
            _view.Refresh();
            SetStatus($"Added {document.FileName}");
            return;
        }

        var hadThumbnail = existing.Thumbnail is not null;
        existing.Apply(document);

        // An Updated event is usually the analysis finishing, so this is the thumbnail arriving.
        if (document.HasThumbnail && !hadThumbnail)
        {
            _ = LoadThumbnailAsync(existing);
        }

        if (ReferenceEquals(DocumentList.SelectedItem, existing))
        {
            ShowMetadata(existing);
        }
    }

    /// <summary>
    /// Brings the live tiles in line with what the server reports: new streams appear, running
    /// ones update in place, and anything that has stopped is removed. A live tile is transient by
    /// nature, so it is never left behind as a stale entry.
    /// </summary>
    private async Task RefreshLiveAsync()
    {
        if (_api is null)
        {
            return;
        }

        LiveListResponse live;

        try
        {
            live = await _api.ListLiveAsync(_connection.Token);
        }
        catch (RpcException ex) when (ex.StatusCode == RpcStatusCode.Unauthenticated)
        {
            // The one failure worth naming: everything live is guarded by the same token, so an
            // empty Streams tab would otherwise look like a server with nothing on air.
            SetStatus($"{_api.Address} refused the token. Put the server's Live__Token in the Token box and connect again.");
            return;
        }
        catch (Exception)
        {
            // A poll that fails changes nothing on screen; the next one will tell the truth.
            return;
        }

        _consumptionUrl = ConsumptionAddress(live);

        var running = live.Streams.ToDictionary(stream => stream.Name, StringComparer.Ordinal);

        foreach (var stale in _streams.Where(item => !running.ContainsKey(item.Id)).ToList())
        {
            if (ReferenceEquals(StreamList.SelectedItem, stale))
            {
                // The stream being watched is gone; stop playing something that is over.
                StopPlayback();
                StreamList.SelectedItem = null;
            }

            _streams.Remove(stale);
            SetStatus($"Live stream ended: {stale.FileName}");
        }

        foreach (var stream in live.Streams)
        {
            var existing = _streams.FirstOrDefault(item => item.Id == stream.Name);

            if (existing is null)
            {
                var item = DocumentItem.ForLive(stream);

                // Newest first: the stream that just started is the one being looked for.
                _streams.Insert(0, item);
                _ = LoadLivePreviewAsync(item);

                SetStatus($"Live stream started: {stream.Name}");
                continue;
            }

            var wasInterrupted = existing.IsInterrupted;

            existing.Apply(stream);

            // A tile that vanishes and returns is worse than one showing a state, and after a
            // resume it would be the same stream on both sides of the gap.
            if (wasInterrupted != existing.IsInterrupted)
            {
                SetStatus(existing.IsInterrupted
                    ? $"Live stream interrupted: {stream.Name}"
                    : $"Live stream resumed: {stream.Name}");
            }

            // Refetched every tick, not just the first time. The server keeps a fresh picture on
            // the same cadence, so a tile that kept its first frame would show a stream that has
            // been running for an hour as it looked in its first second.
            if (stream.HasPreview)
            {
                _ = LoadLivePreviewAsync(existing);
            }

            if (ReferenceEquals(StreamList.SelectedItem, existing))
            {
                ShowMetadata(existing);
                UpdateLiveButtons();

                // KLV can start arriving after the stream has, so the panel appears when it does.
                if (PlayerHost.Visibility == Visibility.Visible)
                {
                    SyncKlv(existing);
                }
            }
        }

        NoStreamsHint.Visibility = _streams.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task LoadLivePreviewAsync(DocumentItem item)
    {
        if (_api is null)
        {
            return;
        }

        try
        {
            var bytes = await _api.DownloadLivePreviewAsync(item.Id, _connection.Token);

            if (bytes is not null)
            {
                item.Thumbnail = LoadBitmap(bytes);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            // A preview is decoration; the tile keeps its icon.
        }
    }

    /// <summary>
    /// Where this server's consumption port is.
    ///
    /// Configuration wins, because only it knows about a load balancer or an ingress in front. With
    /// nothing configured, the host this client already reached plus the port the server reports is
    /// a better answer than refusing to play: it is right for a local run and for Compose, which is
    /// where nobody has configured anything.
    /// </summary>
    private string ConsumptionAddress(LiveListResponse live)
    {
        if (live.ConsumptionUrl is { Length: > 0 } configured)
        {
            return configured;
        }

        if (live.ConsumptionPort <= 0 || _api is null || !Uri.TryCreate(_api.Address, UriKind.Absolute, out var server))
        {
            return string.Empty;
        }

        return $"srt://{server.Host}:{live.ConsumptionPort}";
    }

    /// <summary>
    /// Where a player pulls a live stream: the consumption port, over SRT. The stream it wants is
    /// named separately, as a demuxer option rather than in this URL; see
    /// <see cref="FlyleafEngine.TuneFor"/> for why that distinction matters.
    /// </summary>
    private string? PlaybackUrl()
        => string.IsNullOrWhiteSpace(_consumptionUrl)
            ? null
            : $"{_consumptionUrl.TrimEnd('/')}?mode=caller";

    /// <summary>
    /// The stream identifier naming what to pull, in the SRT Access Control convention: the same
    /// shape an encoder presents on the way in, with the intent reversed.
    /// </summary>
    private static string Identifier(DocumentItem item) => $"#!::r={item.Id},m=request";

    /// <summary>Keeps the grid in name order without re-sorting and rebuilding the whole list.</summary>
    private void InsertSorted(DocumentItem item)
    {
        for (var index = 0; index < _documents.Count; index++)
        {
            if (string.Compare(_documents[index].FileName, item.FileName, StringComparison.OrdinalIgnoreCase) > 0)
            {
                _documents.Insert(index, item);
                return;
            }
        }

        _documents.Add(item);
    }

    private async Task RefreshAsync()
    {
        if (_api is null)
        {
            return;
        }

        IReadOnlyList<DocumentMessage> documents;
        try
        {
            documents = await _api.ListAsync(_connection.Token);
        }
        catch (Exception ex)
        {
            SetStatus($"List failed: {ex.Message}");
            return;
        }

        _documents.Clear();
        foreach (var document in documents.OrderBy(d => d.FileName, StringComparer.OrdinalIgnoreCase))
        {
            var item = new DocumentItem(document);
            _documents.Add(item);
            _ = LoadThumbnailAsync(item);
        }

        UpdateFilterStatus();
    }

    private async Task LoadThumbnailAsync(DocumentItem item)
    {
        if (_api is null || item.IsPending || item.IsLive || !item.HasThumbnail)
        {
            return;
        }

        try
        {
            var bytes = await _api.DownloadThumbnailAsync(item.Id, _connection.Token);
            if (bytes is not null)
            {
                item.Thumbnail = LoadBitmap(bytes);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            // A thumbnail is decoration. The tile keeps its type icon and the list stays usable.
        }
    }

    /// <summary>Decodes from bytes so no file stays locked by the image.</summary>
    private static BitmapImage LoadBitmap(byte[] bytes)
    {
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = new MemoryStream(bytes);
        bitmap.EndInit();
        bitmap.Freeze();

        return bitmap;
    }

    private async void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Selecting in one list clears the other, which raises this again with nothing selected
        // there. Acting only on the list that gained a selection keeps that second pass from
        // immediately wiping the preview the first one just set up.
        if (e.AddedItems.Count > 0)
        {
            if (ReferenceEquals(sender, StreamList))
            {
                DocumentList.SelectedItem = null;
            }
            else if (ReferenceEquals(sender, DocumentList))
            {
                StreamList.SelectedItem = null;
            }
        }

        StopPlayback();
        HideAllPreviews();
        UpdateLiveButtons();

        if (Selected is not { } item)
        {
            PreviewTitle.Text = "Select a stream or a document";
            return;
        }

        if (item.IsPending)
        {
            PreviewTitle.Text = $"{item.FileName}   (uploading)";
            return;
        }

        if (item.IsLive)
        {
            PreviewTitle.Text = item.IsInterrupted
                ? $"{item.FileName}   (interrupted, from {item.Live!.Owner})"
                : $"{item.FileName}   (live from {item.Live!.Owner})";

            ShowMetadata(item);
            UpdateLiveButtons();

            if (PlaybackUrl() is not { } url)
            {
                // The server has not been told its own consumption address, so it cannot hand out
                // one a player could reach. Saying so beats a player failing on an empty address.
                ShowFallback(
                    "This server has no consumption address configured, so the stream cannot be "
                    + "played from here. Set Live:PublicConsumptionUrl.");

                return;
            }

            // Played straight from the SRT address: no download, and the player joins at the live
            // edge because that is where the server starts it.
            StartPlayback(url, live: true, Identifier(item));

            // The banner rides the item, so a marking that changes mid-stream follows it.
            MarkingBanner.DataContext = item;
            SyncKlv(item);
            return;
        }

        PreviewTitle.Text = $"{item.FileName}   ({DocumentItem.HumanSize(item.Size)})";
        ShowMetadata(item);

        // A long recording is streamed rather than fetched. The content endpoint serves byte
        // ranges and a recording written in pieces is seekable end to end, so scrubbing through
        // six hours costs one ranged read rather than six hours of downloading.
        if (StreamUrl(item) is { } streaming)
        {
            StartPlayback(streaming);
            return;
        }

        try
        {
            var path = await _api!.DownloadToCacheAsync(item.Document!, _connection.Token);
            ShowPreview(item, path);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            ShowFallback($"Could not open {item.FileName}: {ex.Message}");
        }
    }

    /// <summary>
    /// Where to stream a document from, or null when downloading it is the better answer.
    ///
    /// Null unless the server has been told its own address, since a URL a player cannot reach is
    /// worse than a slow download.
    /// </summary>
    private string? StreamUrl(DocumentItem item)
        => item.Kind is DocumentKind.Video or DocumentKind.Audio
            && item.Size >= StreamRatherThanDownloadBytes
            && _contentBaseUrl is { Length: > 0 }
                ? $"{_contentBaseUrl.TrimEnd('/')}/api/documents/{item.Id}/content"
                : null;

    /// <summary>
    /// Turns the sensor panel on for a stream that carries KLV and off for one that does not,
    /// which is also what a server too old to answer <c>GetLiveKlv</c> reports.
    /// </summary>
    private void SyncKlv(DocumentItem item)
    {
        if (item.Live?.HasKlv != true)
        {
            StopKlv();
            return;
        }

        if (_klvTimer.IsEnabled)
        {
            return;
        }

        KlvPanel.Visibility = Visibility.Visible;
        _klvTimer.Start();
        _ = RefreshKlvAsync();
    }

    private void StopKlv()
    {
        _klvTimer.Stop();
        KlvPanel.Visibility = Visibility.Collapsed;
        KlvList.ItemsSource = null;
        KlvHint.Visibility = Visibility.Collapsed;
    }

    /// <summary>The newest packet's fields, once a second, for the stream on screen and no other.</summary>
    private async Task RefreshKlvAsync()
    {
        if (_api is null || Selected is not { IsLive: true } item || item.Live?.HasKlv != true)
        {
            StopKlv();
            return;
        }

        LiveKlvMessage? klv;

        try
        {
            klv = await _api.GetLiveKlvAsync(item.Id, _connection.Token);
        }
        catch (Exception)
        {
            // One poll that failed says nothing about the stream; the next second tells the truth.
            return;
        }

        // The selection can move while a call is in flight, and the rows belong to the stream
        // they were asked for.
        if (!ReferenceEquals(Selected, item))
        {
            return;
        }

        KlvList.ItemsSource = klv is null ? null : DocumentItem.KlvRows(klv);
        KlvHint.Text = "No KLV packet has arrived yet.";
        KlvHint.Visibility = klv is null ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowMetadata(DocumentItem item)
    {
        MetadataList.ItemsSource = item.Metadata;
        MetadataPanel.Visibility = item.Metadata.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowPreview(DocumentItem item, string path)
    {
        switch (item.Kind)
        {
            case DocumentKind.Image:
                ImagePreview.Source = LoadBitmap(File.ReadAllBytes(path));
                ImagePreview.Visibility = Visibility.Visible;
                break;

            case DocumentKind.Video:
            case DocumentKind.Audio:
                StartPlayback(path);
                break;

            case DocumentKind.Text:
                var info = new FileInfo(path);
                TextPreview.Text = info.Length > TextPreviewLimit
                    ? ReadHead(path) + $"\r\n\r\n--- truncated at {TextPreviewLimit / 1024} KB ---"
                    : File.ReadAllText(path);
                TextPreview.Visibility = Visibility.Visible;
                break;

            case DocumentKind.Pdf:
                // WPF ships no PDF renderer, and pulling one in for a demo is not worth it.
                ShowFallback("PDF preview opens in the system viewer.");
                break;

            default:
                ShowFallback($"No preview for {item.Document!.ContentType}.");
                break;
        }
    }

    /// <summary>
    /// Plays the cached copy rather than streaming from the API, so scrubbing lands instantly
    /// instead of waiting on a range request per drag.
    /// </summary>
    private void StartPlayback(string path, bool live = false, string? streamId = null)
    {
        try
        {
            FlyleafEngine.EnsureStarted();

            if (_player is null)
            {
                _player = new Player();
                PlayerHost.Player = _player;

                // Opening is asynchronous and reports failure through this and nowhere else.
                // Without it a stream the player cannot reach, or a codec it cannot decode, is a
                // black rectangle and no explanation at all.
                _player.OpenCompleted += OnOpenCompleted;
            }

            FlyleafEngine.TuneFor(_player, live, streamId);
        }
        catch (Exception ex)
        {
            ShowFallback($"The video engine could not start: {ex.Message}");
            return;
        }

        PlayerHost.Visibility = Visibility.Visible;
        PlayerControls.Visibility = Visibility.Visible;

        _suppressSeek = true;
        PositionSlider.Value = 0;
        _suppressSeek = false;

        PositionText.Text = "0:00";
        DurationText.Text = "0:00";

        _player.Audio.Volume = (int)VolumeSlider.Value;
        _player.OpenAsync(path);

        _positionTimer.Start();
        PlayPauseButton.Content = "Pause";
    }

    /// <summary>
    /// Says why playback did not start, on the UI thread.
    ///
    /// The player raises this from its own thread, and a failure is the one case where the video
    /// area stays empty, so it is also the one case where saying nothing is worst.
    /// </summary>
    private void OnOpenCompleted(object? sender, OpenCompletedArgs e)
    {
        if (e.Success)
        {
            return;
        }

        Dispatcher.BeginInvoke(() =>
        {
            var reason = string.IsNullOrWhiteSpace(e.Error) ? "the player gave no reason" : e.Error;

            PlayerHost.Visibility = Visibility.Collapsed;
            PlayerControls.Visibility = Visibility.Collapsed;
            MarkingBanner.DataContext = null;
            StopKlv();

            ShowFallback($"Could not play {e.Url}: {reason}");
            SetStatus($"Playback failed: {reason}");
        });
    }

    private void StopPlayback()
    {
        _positionTimer.Stop();
        StopKlv();

        // Releases the cached file, which a delete would otherwise fail to remove.
        _player?.Stop();

        PlayerHost.Visibility = Visibility.Collapsed;
        PlayerControls.Visibility = Visibility.Collapsed;

        // Nothing is on screen to be marked, and a marking left over one is worse than none.
        MarkingBanner.DataContext = null;
    }

    private void OnPositionTick(object? sender, EventArgs e)
    {
        if (_player is null || _isScrubbing)
        {
            return;
        }

        var duration = TimeSpan.FromTicks(_player.Duration);
        var position = TimeSpan.FromTicks(_player.CurTime);

        DurationText.Text = Format(duration);
        PositionText.Text = Format(position);

        if (duration > TimeSpan.Zero)
        {
            _suppressSeek = true;
            PositionSlider.Value = position.TotalMilliseconds / duration.TotalMilliseconds * 1000;
            _suppressSeek = false;
        }

        PlayPauseButton.Content = _player.Status == Status.Playing ? "Pause" : "Play";
    }

    private void OnSeekStarted(object sender, MouseButtonEventArgs e) => _isScrubbing = true;

    private void OnSeekFinished(object sender, MouseButtonEventArgs e)
    {
        _isScrubbing = false;
        SeekToSliderPosition();
    }

    /// <summary>Fires while dragging too, so the picture follows the thumb rather than jumping
    /// only once it is released.</summary>
    private void OnSeekValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressSeek || _player is null)
        {
            return;
        }

        if (_isScrubbing)
        {
            var duration = TimeSpan.FromTicks(_player.Duration);
            PositionText.Text = Format(duration * (PositionSlider.Value / 1000));
        }

        SeekToSliderPosition();
    }

    private void SeekToSliderPosition()
    {
        if (_player is null || _player.Duration <= 0)
        {
            return;
        }

        var target = TimeSpan.FromTicks(_player.Duration) * (PositionSlider.Value / 1000);
        _player.SeekAccurate((int)target.TotalMilliseconds);
    }

    private void OnPlayPause(object sender, RoutedEventArgs e)
    {
        _player?.TogglePlayPause();
        PlayPauseButton.Content = _player?.Status == Status.Playing ? "Pause" : "Play";
    }

    private void OnStop(object sender, RoutedEventArgs e)
    {
        _player?.Stop();
        PlayPauseButton.Content = "Play";
    }

    private void OnSkipBack(object sender, RoutedEventArgs e) => Skip(-SkipStep);

    private void OnSkipForward(object sender, RoutedEventArgs e) => Skip(SkipStep);

    private void Skip(TimeSpan offset)
    {
        if (_player is null || _player.Duration <= 0)
        {
            return;
        }

        var target = TimeSpan.FromTicks(_player.CurTime) + offset;
        var duration = TimeSpan.FromTicks(_player.Duration);

        if (target < TimeSpan.Zero)
        {
            target = TimeSpan.Zero;
        }
        else if (target > duration)
        {
            target = duration;
        }

        _player.SeekAccurate((int)target.TotalMilliseconds);
    }

    private void OnToggleMute(object sender, RoutedEventArgs e)
    {
        if (_player is null)
        {
            return;
        }

        _player.Audio.Mute = !_player.Audio.Mute;
        MuteButton.Content = _player.Audio.Mute ? "Unmute" : "Mute";
    }

    private void OnVolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_player is not null)
        {
            _player.Audio.Volume = (int)e.NewValue;
        }
    }

    private void OnSpeedChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_player is null || SpeedBox.SelectedItem is not ComboBoxItem { Content: string label })
        {
            return;
        }

        _player.Speed = double.Parse(label.TrimEnd('x'), System.Globalization.CultureInfo.InvariantCulture);
    }

    private void OnToggleFullScreen(object sender, RoutedEventArgs e)
        => PlayerHost.IsFullScreen = !PlayerHost.IsFullScreen;

    /// <summary>The shortcuts a video player is expected to have.</summary>
    private void OnShortcut(object sender, KeyEventArgs e)
    {
        // Typing an address or scrolling a text preview must not trigger transport controls.
        if (_player is null
            || PlayerControls.Visibility != Visibility.Visible
            || Keyboard.FocusedElement is TextBox or ComboBox)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Space:
                OnPlayPause(sender, e);
                break;

            case Key.Left:
                Skip(-SkipStep);
                break;

            case Key.Right:
                Skip(SkipStep);
                break;

            case Key.Up:
                VolumeSlider.Value = Math.Min(100, VolumeSlider.Value + 5);
                break;

            case Key.Down:
                VolumeSlider.Value = Math.Max(0, VolumeSlider.Value - 5);
                break;

            case Key.M:
                OnToggleMute(sender, e);
                break;

            case Key.F:
                PlayerHost.IsFullScreen = !PlayerHost.IsFullScreen;
                break;

            case Key.Escape when PlayerHost.IsFullScreen:
                PlayerHost.IsFullScreen = false;
                break;

            default:
                return;
        }

        e.Handled = true;
    }

    private static string Format(TimeSpan value)
        => value >= TimeSpan.FromHours(1)
            ? value.ToString(@"h\:mm\:ss")
            : value.ToString(@"m\:ss");

    private static string ReadHead(string path)
    {
        using var reader = new StreamReader(path);
        var buffer = new char[TextPreviewLimit];
        var read = reader.Read(buffer, 0, buffer.Length);

        return new string(buffer, 0, read);
    }

    private void ShowFallback(string message)
    {
        FallbackText.Text = message;
        FallbackPreview.Visibility = Visibility.Visible;
    }

    private void HideAllPreviews()
    {
        ImagePreview.Visibility = Visibility.Collapsed;
        ImagePreview.Source = null;
        TextPreview.Visibility = Visibility.Collapsed;
        TextPreview.Clear();
        FallbackPreview.Visibility = Visibility.Collapsed;
        MetadataPanel.Visibility = Visibility.Collapsed;
        MetadataList.ItemsSource = null;
    }

    private async void OnUpload(object sender, RoutedEventArgs e)
    {
        if (_api is null)
        {
            return;
        }

        var dialog = new OpenFileDialog { Title = "Upload to the document store", Multiselect = true };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        // Uploads land among the documents, so show that tab rather than leaving the tiles
        // appearing behind a tab the user is not looking at.
        SidebarTabs.SelectedItem = DocumentsTab;

        // Every tile appears at once, before a single byte has been sent.
        var pending = dialog.FileNames
            .Select(path => (Path: path, Item: DocumentItem.Pending(path)))
            .ToList();

        foreach (var (_, item) in pending)
        {
            InsertSorted(item);
        }

        foreach (var (path, item) in pending)
        {
            try
            {
                var document = await _api.UploadAsync(path, _connection.Token);

                // The same tile becomes the real document; nothing jumps or is rebuilt.
                item.Apply(document);
                SetStatus($"Uploaded {document.FileName} ({DocumentItem.HumanSize(document.Size)}).");
            }
            catch (Exception ex)
            {
                // The optimistic tile has to go: the document does not exist.
                _documents.Remove(item);
                SetStatus($"Upload of {Path.GetFileName(path)} failed: {ex.Message}");
            }
        }
    }

    private async void OnRefresh(object sender, RoutedEventArgs e) => await RefreshAsync();

    /// <summary>
    /// Starts a recording, or stops the one running.
    ///
    /// It reaches back into the server's buffer, so what lands begins a few seconds before this
    /// click: an event already under way when somebody noticed it is still caught. A second click
    /// while one is running stops it; a further trigger from anywhere else would extend it rather
    /// than start a second file.
    /// </summary>
    private async void OnRecord(object sender, RoutedEventArgs e)
    {
        if (_api is null || Selected is not { IsLive: true } item)
        {
            return;
        }

        try
        {
            if (item.IsRecording)
            {
                await _api.StopLiveRecordingAsync(item.Id, _connection.Token);
                SetStatus($"Stopping the recording of {item.FileName}; it becomes a document when it ends.");
            }
            else
            {
                // Zero means the server's default duration. It keeps running whether or not this
                // client is here, so nothing has to be held open.
                var recording = await _api.RecordLiveAsync(item.Id, 0, _connection.Token);

                SetStatus(recording is null
                    ? $"{item.FileName} could not be recorded."
                    : $"Recording {item.FileName} until "
                        + $"{recording.EndsAt.ToDateTimeOffset().LocalDateTime:HH:mm:ss}.");
            }

            await RefreshLiveAsync();
        }
        catch (Exception ex)
        {
            SetStatus($"Recording failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Stores a full-resolution picture of the stream as a document. Decoded fresh on the server
    /// rather than lifted from the tile, which is both smaller and several seconds older.
    /// </summary>
    private async void OnSnapshot(object sender, RoutedEventArgs e)
    {
        if (_api is null || Selected is not { IsLive: true } item)
        {
            return;
        }

        try
        {
            var id = await _api.SnapshotLiveAsync(item.Id, _connection.Token);

            SetStatus(id is null
                ? $"{item.FileName} had nothing to capture."
                : $"Snapshot of {item.FileName} stored.");

            if (id is not null)
            {
                await RefreshAsync();
            }
        }
        catch (Exception ex)
        {
            SetStatus($"Snapshot failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Both buttons act on a stream, so they are off for a document, and the record button says
    /// which way it will act.
    /// </summary>
    private void UpdateLiveButtons()
    {
        var stream = Selected is { IsLive: true } item ? item : null;

        RecordButton.IsEnabled = stream is not null;
        SnapshotButton.IsEnabled = stream is not null;
        RecordButton.Content = stream?.IsRecording == true ? "Stop recording" : "Record";
    }

    private async void OnDelete(object sender, RoutedEventArgs e)
    {
        if (_api is null || Selected is not { IsPending: false, IsLive: false } item)
        {
            return;
        }

        var confirmed = MessageBox.Show(
            this,
            $"Delete {item.FileName}?",
            "Storage Demo",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);

        if (confirmed != MessageBoxResult.OK)
        {
            return;
        }

        // Release the cached copy first, or the player keeps the file locked.
        StopPlayback();

        var index = _documents.IndexOf(item);
        _documents.Remove(item);

        try
        {
            await _api.DeleteAsync(item.Id, _connection.Token);
            SetStatus($"Deleted {item.FileName}.");
        }
        catch (Exception ex)
        {
            // Put it back rather than lying about what the store holds.
            _documents.Insert(Math.Clamp(index, 0, _documents.Count), item);
            SetStatus($"Delete failed: {ex.Message}");
        }
    }

    private async void OnOpenExternally(object sender, RoutedEventArgs e)
    {
        if (_api is null || Selected is not { IsPending: false, IsLive: false } item)
        {
            return;
        }

        try
        {
            var path = await _api.DownloadToCacheAsync(item.Document!, _connection.Token);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            SetStatus($"Could not open externally: {ex.Message}");
        }
    }

    private async void OnSaveAs(object sender, RoutedEventArgs e)
    {
        if (_api is null || Selected is not { IsPending: false, IsLive: false } item)
        {
            return;
        }

        var dialog = new SaveFileDialog { FileName = item.FileName };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            var path = await _api.DownloadToCacheAsync(item.Document!, _connection.Token);
            File.Copy(path, dialog.FileName, overwrite: true);
            SetStatus($"Saved to {dialog.FileName}.");
        }
        catch (Exception ex)
        {
            SetStatus($"Save failed: {ex.Message}");
        }
    }

    private void SetStatus(string message)
        => Dispatcher.Invoke(() => StatusText.Text = $"{DateTime.Now:HH:mm:ss}  {message}");
}
