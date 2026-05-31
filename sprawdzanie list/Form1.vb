Imports System.Collections.Concurrent
Imports System.Drawing
Imports System.IO
Imports System.Net
Imports System.Net.Http
Imports System.Net.NetworkInformation
Imports System.Net.Sockets
Imports System.Net.WebSockets
Imports System.Text
Imports System.Text.Json
Imports System.Text.RegularExpressions
Imports System.Threading
Imports System.Threading.Tasks
Imports System.Windows.Forms
Public Class Form1
    Inherits Form
    Private stationCache As New Concurrent.ConcurrentDictionary(Of String, (Boolean, String))
    Private dgvStations As DataGridView
    Private btnSelectFile As Button
    Private btnSelectCountry As Button
    Private btnSave As Button
    Private txtLog As TextBox
    Private progressBar As ProgressBar
    Private lblProgress As Label
    Private lblUpdateStatus As Label
    Private btnReset As Button
    Private btnEditList As Button
    Private btnSendToRadio As Button
    Private WithEvents btnDownloadFromRadio As Button
    Private WithEvents languageSelector As ComboBox
    Private btnBuyCoffee As Button
    Private btnSearchRadioAgain As Button
    Private Const MAX_RETRIES As Integer = 3
    Private Const RETRY_DELAY As Integer = 1000
    Private Const MAX_PARALLEL As Integer = 800
    Private Const STREAM_CHECK_TIMEOUT As Integer = 4 ' 4s — HEAD jest szybki, GET potrzebuje trochę więcej czasu
    ' Cache trzymamy obok exe — przenośne (kopiujesz exe na inny PC, cache jedzie razem).
    Private Shared ReadOnly AppDataDir As String = AppDomain.CurrentDomain.BaseDirectory
    Private ReadOnly CacheFilePath As String = Path.Combine(AppDataDir, "stations_cache.json")
    Private Const CACHE_MAX_AGE_HOURS As Integer = 24
    Private ReadOnly UrlCheckCachePath As String = Path.Combine(AppDataDir, "url_cache.dat")
    Private Const URL_CACHE_DAYS As Integer = 7
    ' Cache pełnej bazy yoRadio (pobieranej raz, bo wymaga obejścia wszystkich krajów).
    Private yoRadioCache As List(Of Station) = Nothing

    ' ====== Wbudowany odtwarzacz — P/Invoke bezpośrednio do libvlc.dll ======
    ' Pomija LibVLCSharp wrapper, który nie inicjalizował się na tym systemie.
    ' Stany VLC: 0=Idle 1=Opening 2=Buffering 3=Playing 4=Paused 5=Stopping 6=Ended 7=Error

    <Runtime.InteropServices.DllImport("kernel32.dll", SetLastError:=True, CharSet:=Runtime.InteropServices.CharSet.Auto)>
    Private Shared Function SetDllDirectory(lpPathName As String) As Boolean
    End Function

    <Runtime.InteropServices.DllImport("libvlc", CallingConvention:=Runtime.InteropServices.CallingConvention.Cdecl)>
    Private Shared Function libvlc_new(argc As Integer, <Runtime.InteropServices.MarshalAs(Runtime.InteropServices.UnmanagedType.LPArray, ArraySubType:=Runtime.InteropServices.UnmanagedType.LPStr)> argv() As String) As IntPtr
    End Function

    <Runtime.InteropServices.DllImport("libvlc", CallingConvention:=Runtime.InteropServices.CallingConvention.Cdecl, CharSet:=Runtime.InteropServices.CharSet.Ansi)>
    Private Shared Function libvlc_media_new_location(p_instance As IntPtr, psz_mrl As String) As IntPtr
    End Function

    <Runtime.InteropServices.DllImport("libvlc", CallingConvention:=Runtime.InteropServices.CallingConvention.Cdecl)>
    Private Shared Function libvlc_media_player_new_from_media(p_md As IntPtr) As IntPtr
    End Function

    <Runtime.InteropServices.DllImport("libvlc", CallingConvention:=Runtime.InteropServices.CallingConvention.Cdecl)>
    Private Shared Function libvlc_media_player_play(p_mi As IntPtr) As Integer
    End Function

    <Runtime.InteropServices.DllImport("libvlc", CallingConvention:=Runtime.InteropServices.CallingConvention.Cdecl)>
    Private Shared Sub libvlc_media_player_stop(p_mi As IntPtr)
    End Sub

    <Runtime.InteropServices.DllImport("libvlc", CallingConvention:=Runtime.InteropServices.CallingConvention.Cdecl)>
    Private Shared Sub libvlc_media_player_pause(p_mi As IntPtr)
    End Sub

    <Runtime.InteropServices.DllImport("libvlc", CallingConvention:=Runtime.InteropServices.CallingConvention.Cdecl)>
    Private Shared Function libvlc_media_player_get_state(p_mi As IntPtr) As Integer
    End Function

    <Runtime.InteropServices.DllImport("libvlc", CallingConvention:=Runtime.InteropServices.CallingConvention.Cdecl)>
    Private Shared Function libvlc_audio_set_volume(p_mi As IntPtr, i_volume As Integer) As Integer
    End Function

    <Runtime.InteropServices.DllImport("libvlc", CallingConvention:=Runtime.InteropServices.CallingConvention.Cdecl)>
    Private Shared Sub libvlc_media_release(p_md As IntPtr)
    End Sub

    <Runtime.InteropServices.DllImport("libvlc", CallingConvention:=Runtime.InteropServices.CallingConvention.Cdecl)>
    Private Shared Sub libvlc_media_player_release(p_mi As IntPtr)
    End Sub

    <Runtime.InteropServices.DllImport("libvlc", CallingConvention:=Runtime.InteropServices.CallingConvention.Cdecl)>
    Private Shared Function libvlc_media_player_get_media(p_mi As IntPtr) As IntPtr
    End Function


    ' Struktura statystyk VLC (libvlc_media_stats_t) — kolejność pól musi zgadzać się z VLC 3.x.
    <Runtime.InteropServices.StructLayout(Runtime.InteropServices.LayoutKind.Sequential)>
    Private Structure VlcStats
        Public i_read_bytes As Integer
        Public f_input_bitrate As Single    ' bajty/sek — mnożymy *8/1000 → kbps
        Public i_demux_read_bytes As Integer
        Public f_demux_bitrate As Single
        Public i_demux_corrupted As Integer
        Public i_demux_discontinuity As Integer
        Public i_decoded_video As Integer
        Public i_decoded_audio As Integer
        Public i_displayed_pictures As Integer
        Public i_lost_pictures As Integer
        Public i_played_abuffers As Integer
        Public i_lost_abuffers As Integer
        Public i_sent_packets As Integer
        Public i_sent_bytes As Integer
        Public f_send_bitrate As Single
    End Structure

    <Runtime.InteropServices.DllImport("libvlc", CallingConvention:=Runtime.InteropServices.CallingConvention.Cdecl)>
    Private Shared Function libvlc_media_get_stats(p_md As IntPtr, ByRef p_stats As VlcStats) As Integer
    End Function

    Private _vlcInstance As IntPtr = IntPtr.Zero
    Private _vlcMediaPlayer As IntPtr = IntPtr.Zero
    Private _wmpTimer As Windows.Forms.Timer
    Private _wmpCurrentName As String = ""
    Private _wmpNowPlayingLbl As Label = Nothing
    Private _wmpStatusLbl As Label = Nothing
    Private _wmpStatusDot As Label = Nothing
    Private _wmpBitrateLbl As Label = Nothing
    Private _wmpPlayPauseBtn As Button = Nothing
    Private _wmpBlinkState As Boolean = False
    Private _currentPlayUrl As String = ""
    Private _prevReadBytes As Long = 0
    Private _prevReadTick As Long = 0
    Private _smoothKbps As Integer = 0


    ' Katalog w AppData użytkownika gdzie wypakowujemy wbudowany VLC.
    ' AppData jest bardziej trwałe niż temp — Windows nie czyści tego automatycznie.
    Private Shared ReadOnly VlcTempDir As String =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                     "sprawdzanie-list", "vlc")

    ' Wypakowuje wbudowane pliki cache (stations_cache.json, url_cache.dat) obok exe,
    ' ale TYLKO jeśli jeszcze nie istnieją — nie nadpisujemy istniejących danych użytkownika.
    Private Sub ExtractCacheIfNeeded()
        Dim ns = "sprawdzanie_list."
        For Each pair In {
            (ns & "stations_cache.json", CacheFilePath),
            (ns & "url_cache.dat", UrlCheckCachePath)
        }
            If File.Exists(pair.Item2) Then Continue For   ' już istnieje — nie dotykamy
            Try
                Using stream = GetType(Form1).Assembly.GetManifestResourceStream(pair.Item1)
                    If stream Is Nothing Then Continue For
                    Using fs As New FileStream(pair.Item2, FileMode.Create, FileAccess.Write)
                        stream.CopyTo(fs)
                    End Using
                End Using
            Catch
            End Try
        Next
    End Sub

    ' Wypakowuje vlc_x86.zip z zasobów exe do folderu temp.
    ' Jeśli już wypakowany (plik marker istnieje) — nic nie robi (szybki start).
    Private Sub ExtractVlcIfNeeded()
        Try
            Dim marker = Path.Combine(VlcTempDir, "vlc.ok")
            If File.Exists(marker) Then Return   ' juz wypakowany
            AppendLog("VLC: wypakowywanie do temp (raz, przy pierwszym uruchomieniu)...")
            If Directory.Exists(VlcTempDir) Then Directory.Delete(VlcTempDir, True)
            Directory.CreateDirectory(VlcTempDir)
            ' Nazwa zasobu: {RootNamespace}.{NazwaPliku}
            Dim resName = "sprawdzanie_list.vlc_x86.zip"
            Using stream = GetType(Form1).Assembly.GetManifestResourceStream(resName)
                If stream Is Nothing Then
                    AppendLog("VLC: brak zasobu " & resName)
                    Return
                End If
                Using zip As New System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Read)
                    For Each entry In zip.Entries
                        Dim dest = Path.Combine(VlcTempDir, entry.FullName.Replace("/"c, Path.DirectorySeparatorChar))
                        Dim dir = Path.GetDirectoryName(dest)
                        If Not Directory.Exists(dir) Then Directory.CreateDirectory(dir)
                        If Not String.IsNullOrEmpty(entry.Name) Then
                            Using fs As New FileStream(dest, FileMode.Create, FileAccess.Write)
                                Using es = entry.Open()
                                    es.CopyTo(fs)
                                End Using
                            End Using
                        End If
                    Next
                End Using
            End Using
            File.WriteAllText(marker, "ok")
            AppendLog("VLC: wypakowany OK")
        Catch ex As Exception
            AppendLog("VLC extract error: " & ex.Message)
        End Try
    End Sub

    ' Szuka katalogu z libvlc.dll w kolejności: system x86 → system x64 → bundled obok exe.
    ' Na PC z zainstalowanym VLC działa bez żadnych dodatkowych plików obok exe.
    Private Function FindVlcDir() As String
        ' 1) Systemowy VLC x86 (pasuje do naszego x86 exe)
        Dim sys86 = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "VideoLAN", "VLC")
        If File.Exists(Path.Combine(sys86, "libvlc.dll")) Then Return sys86

        ' 2) Systemowy VLC x64 (tylko jeśli exe jest x64)
        If Environment.Is64BitProcess Then
            Dim sys64 = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "VideoLAN", "VLC")
            If File.Exists(Path.Combine(sys64, "libvlc.dll")) Then Return sys64
        End If

        ' 3) Bundlowane DLL-e obok exe (libvlc\win-x86\ lub win-x64\)
        Dim arch = If(Environment.Is64BitProcess, "win-x64", "win-x86")
        Dim bundled = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "libvlc", arch)
        If File.Exists(Path.Combine(bundled, "libvlc.dll")) Then Return bundled

        ' 4) Wypakowany temp (wbudowany zasób)
        If File.Exists(Path.Combine(VlcTempDir, "libvlc.dll")) Then Return VlcTempDir

        ' 5) Fallback: sam katalog exe
        Dim exeDir = AppDomain.CurrentDomain.BaseDirectory
        If File.Exists(Path.Combine(exeDir, "libvlc.dll")) Then Return exeDir

        Return Nothing
    End Function

    ' ── EnsurePlayer / Play / Stop / Volume ────────────────────────────────────
    Private Sub EnsurePlayer()
        If _vlcInstance <> IntPtr.Zero Then Return
        ExtractVlcIfNeeded()
        Dim vlcDir = FindVlcDir()
        If vlcDir Is Nothing Then
            AppendLog("VLC nie znaleziony. Zainstaluj VLC: videolan.org/vlc")
            Return
        End If
        Try
            AppendLog("VLC: " & vlcDir)
            SetDllDirectory(vlcDir)
            Dim curPath = Environment.GetEnvironmentVariable("PATH")
            If Not curPath.Contains(vlcDir) Then
                Environment.SetEnvironmentVariable("PATH", vlcDir & ";" & curPath)
            End If
            Dim pluginsDir = Path.Combine(vlcDir, "plugins")
            If Not Directory.Exists(pluginsDir) Then pluginsDir = vlcDir
            Environment.SetEnvironmentVariable("VLC_PLUGIN_PATH", pluginsDir)
            Dim opts() As String = {"--no-video", "--quiet", "--no-osd"}
            _vlcInstance = libvlc_new(opts.Length, opts)
            If _vlcInstance = IntPtr.Zero Then
                AppendLog("VLC: libvlc_new zwrocil null")
                Return
            End If
            _wmpTimer = New Windows.Forms.Timer With {.Interval = 400}
            AddHandler _wmpTimer.Tick, AddressOf WmpTimerTick
            _wmpTimer.Start()
            AppendLog("VLC: gotowy")
        Catch ex As Exception
            AppendLog("VLC BLAD: " & ex.Message)
            _vlcInstance = IntPtr.Zero
        End Try
    End Sub

    Friend Sub PlayStationWmp(url As String, stationName As String)
        EnsurePlayer()
        _wmpCurrentName = If(String.IsNullOrEmpty(stationName), url, stationName)
        If _vlcInstance = IntPtr.Zero Then
            WmpUpdateStatus("BLAD VLC", Color.FromArgb(220, 40, 40), False, "")
            Return
        End If
        Task.Run(Async Function()
                     Dim playUrl = url
                     Dim lower = url.ToLowerInvariant()
                     If lower.Contains(".pls") OrElse (lower.Contains(".m3u") AndAlso Not lower.Contains(".m3u8")) Then
                         Try
                             Dim info = Await ResolvePlaylistInfoAsync(url)
                             If Not String.IsNullOrWhiteSpace(info.StreamUrl) Then
                                 playUrl = info.StreamUrl
                                 If Not String.IsNullOrWhiteSpace(info.Title) AndAlso String.IsNullOrEmpty(stationName) Then
                                     _wmpCurrentName = CleanStationName(info.Title)
                                 End If
                             End If
                         Catch
                         End Try
                     End If
                     Try
                         If _vlcMediaPlayer <> IntPtr.Zero Then
                             libvlc_media_player_stop(_vlcMediaPlayer)
                             libvlc_media_player_release(_vlcMediaPlayer)
                             _vlcMediaPlayer = IntPtr.Zero
                         End If
                         Dim playStr = If(playUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase), playUrl, "http://" & playUrl)
                         Dim media = libvlc_media_new_location(_vlcInstance, playStr)
                         If media = IntPtr.Zero Then : AppendLog("VLC: media null") : Return : End If
                         _vlcMediaPlayer = libvlc_media_player_new_from_media(media)
                         libvlc_media_release(media)
                         libvlc_audio_set_volume(_vlcMediaPlayer, 80)
                         _currentPlayUrl = playStr
                         libvlc_media_player_play(_vlcMediaPlayer)
                         AppendLog("VLC: play " & playStr)
                     Catch ex As Exception
                         AppendLog("VLC play error: " & ex.Message)
                         WmpUpdateStatus("BLAD: " & ex.Message, Color.FromArgb(220, 40, 40), False, _wmpCurrentName)
                     End Try
                 End Function)
    End Sub

    Friend Sub StopPlayerWmp()
        Try
            If _vlcMediaPlayer <> IntPtr.Zero Then
                libvlc_media_player_stop(_vlcMediaPlayer)
                libvlc_media_player_release(_vlcMediaPlayer)
                _vlcMediaPlayer = IntPtr.Zero
            End If
        Catch
        End Try
        _wmpCurrentName = ""
        WmpUpdateStatus("STOP", Color.FromArgb(160, 50, 50), False, "")
    End Sub

    Friend Sub TogglePauseWmp()
        If _vlcMediaPlayer = IntPtr.Zero Then Return
        Try : libvlc_media_player_pause(_vlcMediaPlayer) : Catch : End Try
    End Sub

    Friend Sub SetVolumeWmp(vol As Integer)
        Try
            If _vlcMediaPlayer <> IntPtr.Zero Then libvlc_audio_set_volume(_vlcMediaPlayer, vol)
        Catch
        End Try
    End Sub

    Private Sub WmpTimerTick(sender As Object, e As EventArgs)
        If _vlcMediaPlayer = IntPtr.Zero Then Return
        Try
            _wmpBlinkState = Not _wmpBlinkState
            Dim st = libvlc_media_player_get_state(_vlcMediaPlayer)
            Dim isPlaying = (st = 3)
            Dim statusTxt As String
            Dim dotColor As Color
            ' Bitrate: mierzymy SAMI z i_read_bytes między tikami — działa dla każdego strumienia.
            Dim bitrateStr As String = ""
            If st = 3 Then
                Try
                    Dim med = libvlc_media_player_get_media(_vlcMediaPlayer)
                    If med <> IntPtr.Zero Then
                        Dim stats As VlcStats
                        If libvlc_media_get_stats(med, stats) <> 0 Then
                            Dim nowTick = DateTime.UtcNow.Ticks
                            Dim curBytes = CLng(stats.i_read_bytes)
                            If _prevReadTick > 0 AndAlso curBytes >= _prevReadBytes Then
                                Dim elapsedSec = (nowTick - _prevReadTick) / 10_000_000.0
                                If elapsedSec > 0.05 Then
                                    Dim kbps = CInt((curBytes - _prevReadBytes) * 8L / 1000L / elapsedSec)
                                    If kbps > 8 AndAlso kbps < 3000 Then
                                        ' Wygładź wykładniczo (EMA α=0.3) — eliminuje skoki
                                        _smoothKbps = CInt(_smoothKbps * 0.7 + kbps * 0.3)
                                    End If
                                End If
                            End If
                            _prevReadBytes = curBytes
                            _prevReadTick = nowTick
                            If _smoothKbps > 8 Then bitrateStr = "  " & _smoothKbps & " kbps"
                        End If
                    End If
                Catch
                End Try
                ' Fallback z URL/nazwy gdy pomiar jeszcze się nie ustabilizował
                If bitrateStr = "" Then
                    Dim m = Regex.Match(_wmpCurrentName & " " & If(_currentPlayUrl, ""),
                                        "(\d{2,3})\s*(?:kbps|kbit|kbits|k\b)", RegexOptions.IgnoreCase)
                    If m.Success Then bitrateStr = "  " & m.Groups(1).Value & " kbps"
                End If
            Else
                ' Reset przy zatrzymaniu
                _prevReadTick = 0 : _prevReadBytes = 0 : _smoothKbps = 0
            End If

            Select Case st
                Case 3
                    statusTxt = "GRA" & bitrateStr
                    dotColor = Color.FromArgb(0, 200, 80)
                Case 4
                    statusTxt = "PAUZA"
                    dotColor = Color.FromArgb(255, 180, 0)
                Case 2
                    statusTxt = "BUFORUJE..."
                    dotColor = If(_wmpBlinkState, Color.FromArgb(255, 220, 0), Color.FromArgb(80, 80, 30))
                Case 1
                    statusTxt = "LACZE..."
                    dotColor = If(_wmpBlinkState, Color.FromArgb(80, 160, 255), Color.FromArgb(30, 50, 80))
                Case 5, 6
                    statusTxt = "STOP"
                    dotColor = Color.FromArgb(160, 50, 50)
                Case 7
                    statusTxt = "BLAD STRUMIENIA"
                    dotColor = Color.FromArgb(220, 40, 40)
                Case Else
                    statusTxt = "..."
                    dotColor = Color.FromArgb(100, 100, 100)
            End Select
            WmpUpdateStatus(statusTxt, dotColor, isPlaying, _wmpCurrentName)
        Catch
        End Try
    End Sub

    Private Sub WmpUpdateStatus(statusTxt As String, dotColor As Color, isPlaying As Boolean, stationName As String)
        If _wmpStatusLbl IsNot Nothing AndAlso Not _wmpStatusLbl.IsDisposed Then
            Try : _wmpStatusLbl.BeginInvoke(Sub() _wmpStatusLbl.Text = statusTxt) : Catch : End Try
        End If
        If _wmpStatusDot IsNot Nothing AndAlso Not _wmpStatusDot.IsDisposed Then
            Try : _wmpStatusDot.BeginInvoke(Sub() _wmpStatusDot.BackColor = dotColor) : Catch : End Try
        End If
        If _wmpNowPlayingLbl IsNot Nothing AndAlso Not _wmpNowPlayingLbl.IsDisposed Then
            Try : _wmpNowPlayingLbl.BeginInvoke(Sub() _wmpNowPlayingLbl.Text = stationName) : Catch : End Try
        End If
        If _wmpPlayPauseBtn IsNot Nothing AndAlso Not _wmpPlayPauseBtn.IsDisposed Then
            Try : _wmpPlayPauseBtn.BeginInvoke(Sub() _wmpPlayPauseBtn.Text = If(isPlaying, ChrW(&H23F8), ChrW(&H25B6))) : Catch : End Try
        End If
    End Sub

    Private Sub WmpUpdateLabel(text As String)
        WmpUpdateStatus(text, Color.FromArgb(100, 100, 100), False, "")
    End Sub

    ' Statystyki sprawdzania — reset na początku każdej sesji sprawdzania.
    Private _statException As Integer = 0    ' oba (HEAD i GET) rzuciły wyjątek
    Private _statNonSuccess As Integer = 0   ' serwer odpowiedział != 2xx
    Private _statMime As Integer = 0         ' 200 ale Content-Type = HTML i brak ICY
    Private _statOkIcy As Integer = 0        ' zaakceptowany przez ICY headers
    Private _statOkMime As Integer = 0       ' zaakceptowany przez Content-Type
    Private _statOkTcp As Integer = 0        ' zaakceptowany przez raw TcpClient (ICY/1.0)

    ' Współdzielony klient HTTP do sprawdzania strimów – jeden na całą aplikację.
    ' Tworzenie nowego HttpClient na każde żądanie wyczerpuje sockety i jest wolne,
    ' dlatego używamy jednej instancji z własnym limitem czasu na żądanie (przez CancellationToken).
    Private Shared ReadOnly streamCheckHandler As HttpClientHandler = New HttpClientHandler() With {
        .AllowAutoRedirect = False,
        .ServerCertificateCustomValidationCallback = Function(a, b, c, d) True,
        .AutomaticDecompression = DecompressionMethods.GZip Or DecompressionMethods.Deflate
    }
    Private Shared ReadOnly streamCheckClient As HttpClient = New HttpClient(streamCheckHandler) With {
        .Timeout = Timeout.InfiniteTimeSpan
    }

    ' ====== Motyw kolorystyczny (nowoczesny, jasny) ======
    Private Shared ReadOnly ThemeBg As Color = Color.FromArgb(245, 247, 250)
    Private Shared ReadOnly ThemeAccent As Color = Color.FromArgb(0, 120, 215)
    Private Shared ReadOnly ThemeAccentDark As Color = Color.FromArgb(0, 90, 170)
    Private Shared ReadOnly ThemeText As Color = Color.FromArgb(33, 37, 41)
    Private Shared ReadOnly ThemeBorder As Color = Color.FromArgb(200, 205, 210)
    Private stations As New List(Of Station)
    Private radioIPs As New List(Of String)
    Private translations As Dictionary(Of String, Dictionary(Of String, String))
    Private currentLanguage As String = "pl"
    Private checkedStationsCount As Integer = 0
    Private totalStationsCount As Integer = 0
    Private isRadioSearchRunning As Boolean = False
    Private radioSearchCts As CancellationTokenSource = Nothing
    Private rssiPanel As FlowLayoutPanel
    Private rssiLabels As New Dictionary(Of String, Label)
    Private Const FILE_TO_CHECK As String = "sprawdzanie list.exe"
    Private Const REPO_PATH As String = "seba99317/sprawdzanie-list"

    Public Sub New()
        ' .NET Framework domyślnie pozwala tylko na 2 połączenia na host – to dławi
        ' równoległe sprawdzanie strimów. Podnosimy limit i wyłączamy zbędne narzuty.
        ServicePointManager.DefaultConnectionLimit = 1000
        ServicePointManager.Expect100Continue = False
        ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 Or SecurityProtocolType.Tls11 Or SecurityProtocolType.Tls
        ServicePointManager.ServerCertificateValidationCallback = Function(senderObj, certificate, chain, sslPolicyErrors) True
        InitializeTranslations()
        Me.Text = translations(currentLanguage)("form_title")
        Me.Size = New Size(600, 550)
        Me.FormBorderStyle = FormBorderStyle.FixedSingle
        Me.MaximizeBox = False
        Me.Icon = My.Resources.icon1

        btnBuyCoffee = New Button With {
        .Text = translations(currentLanguage)("btn_buy_coffee"),
        .AutoSize = True,
        .Padding = New Padding(8, 4, 8, 4),
        .Location = New Point(Me.ClientSize.Width - 120, 10),
        .FlatStyle = FlatStyle.Flat,
        .ForeColor = Color.Red,
        .BackColor = Color.Transparent
    }
        btnBuyCoffee.FlatAppearance.BorderSize = 0
        btnBuyCoffee.FlatAppearance.MouseOverBackColor = Color.Transparent
        btnBuyCoffee.FlatAppearance.MouseDownBackColor = Color.Transparent
        AddHandler btnBuyCoffee.Click, Sub()
                                           Try
                                               System.Diagnostics.Process.Start(New ProcessStartInfo With {
                                               .FileName = "https://buycoffee.to/seba99317",
                                               .UseShellExecute = True
                                           })
                                           Catch ex As Exception
                                               MessageBox.Show(translations(currentLanguage)("msg_cannot_open_link") & ex.Message, translations(currentLanguage)("msg_error_title"), MessageBoxButtons.OK, MessageBoxIcon.Error)
                                           End Try
                                       End Sub
        Me.Controls.Add(btnBuyCoffee)
        Dim blinkTimer As New Windows.Forms.Timer() With {.Interval = 500}
        AddHandler blinkTimer.Tick, Sub()
                                        If btnBuyCoffee.ForeColor = Color.Red Then
                                            btnBuyCoffee.ForeColor = Color.Blue
                                        Else
                                            btnBuyCoffee.ForeColor = Color.Red
                                        End If
                                    End Sub
        blinkTimer.Start()
        AddHandler Me.Resize, Sub()
                                  btnBuyCoffee.Location = New Point(Me.ClientSize.Width - btnBuyCoffee.Width - 10, 10)
                                  lblUpdateStatus.Location = New Point(Me.ClientSize.Width - lblUpdateStatus.Width, 0)
                                  lblUpdateStatus.BringToFront()
                                  languageSelector.Location = New Point(10, Me.ClientSize.Height - 80)
                                  txtLog.Size = New Size(560, Me.ClientSize.Height - 350)
                                  rssiPanel.Location = New Point(10, Me.ClientSize.Height - 50)
                              End Sub
        lblUpdateStatus = New Label With {
        .Location = New Point(Me.ClientSize.Width - 200, 0),
        .Size = New Size(200, 20),
        .Text = translations(currentLanguage)("lbl_update_checking"),
        .TextAlign = ContentAlignment.MiddleRight,
        .AutoSize = True,
        .Visible = False
    }
        Me.Controls.Add(lblUpdateStatus)
        lblUpdateStatus.BringToFront()
        progressBar = New ProgressBar() With {
        .Location = New Point(10, 50),
        .Size = New Size(560, 20),
        .Minimum = 0,
        .Maximum = 100,
        .Value = 0
    }
        Me.Controls.Add(progressBar)
        lblProgress = New Label() With {
        .Location = New Point(10, 75),
        .Size = New Size(560, 20),
        .Text = translations(currentLanguage)("lbl_progress_initial"),
        .TextAlign = ContentAlignment.MiddleLeft,
        .AutoSize = True
    }
        Me.Controls.Add(lblProgress)
        ' DataGridView initialization
        dgvStations = New DataGridView With {
                .RowHeadersVisible = False,   ' <<< usuwa pustą pierwszą kolumnę
        .Location = New Point(10, 100),
        .Size = New Size(560, 120),
        .AllowUserToAddRows = False,
        .AllowUserToDeleteRows = False,
        .ReadOnly = True,
        .SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        .AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill
    }
        ' Add Name and URL columns with higher FillWeight
        Dim nameColumn As New DataGridViewTextBoxColumn With {
        .Name = "Name",
        .HeaderText = translations(currentLanguage)("col_station_name"),
        .FillWeight = 50 ' Higher weight for wider column
    }
        Dim urlColumn As New DataGridViewTextBoxColumn With {
        .Name = "URL",
        .HeaderText = translations(currentLanguage)("col_station_url"),
        .FillWeight = 40 ' Higher weight for wider column
    }
        Dim volumeColumn As New DataGridViewTextBoxColumn With {
        .Name = "Volume",
        .HeaderText = "Volume",
        .MaxInputLength = 5, ' Limit input to 5 characters
        .FillWeight = 10, ' Lower weight for narrower column
        .Width = 50 ' Suggest narrow width
    }
        dgvStations.Columns.Add(nameColumn)
        dgvStations.Columns.Add(urlColumn)
        dgvStations.Columns.Add(volumeColumn)
        Me.Controls.Add(dgvStations)
        Dim topPanel As New FlowLayoutPanel With {
        .Location = New Point(10, 10),
        .Size = New Size(560, 35),
        .FlowDirection = FlowDirection.LeftToRight,
        .AutoSize = True
    }
        Me.Controls.Add(topPanel)
        btnSelectFile = New Button With {
        .Text = translations(currentLanguage)("btn_select_file"),
        .AutoSize = True,
        .Padding = New Padding(10, 5, 10, 5)
    }
        AddHandler btnSelectFile.Click, AddressOf btnSelectFile_Click
        topPanel.Controls.Add(btnSelectFile)
        btnSelectCountry = New Button With {
        .Text = translations(currentLanguage)("btn_select_country"),
        .AutoSize = True,
        .Padding = New Padding(10, 5, 10, 5)
    }
        AddHandler btnSelectCountry.Click, AddressOf btnSelectCountry_Click
        topPanel.Controls.Add(btnSelectCountry)
        btnDownloadFromRadio = New Button With {
        .Text = translations(currentLanguage)("btn_download_from_radio"),
        .AutoSize = True,
        .Padding = New Padding(10, 5, 10, 5),
        .Visible = False
    }
        AddHandler btnDownloadFromRadio.Click, AddressOf btnDownloadFromRadio_Click
        topPanel.Controls.Add(btnDownloadFromRadio)
        btnReset = New Button With {
        .Text = translations(currentLanguage)("btn_reset"),
        .AutoSize = True,
        .Padding = New Padding(10, 5, 10, 5)
    }
        AddHandler btnReset.Click, Sub()
                                       StopPlayerWmp()
                                       stations.Clear()
                                       dgvStations.Rows.Clear()
                                       txtLog.Clear()
                                       progressBar.Value = 0
                                       checkedStationsCount = 0
                                       totalStationsCount = 0
                                       lblProgress.Text = translations(currentLanguage)("lbl_progress_initial")
                                       AppendLog(translations(currentLanguage)("log_reset_app"))
                                       Try
                                           If File.Exists(CacheFilePath) Then
                                               File.Delete(CacheFilePath)
                                               AppendLog("Cache usuniety – przy nastepnym pobraniu pelne odswiezenie.")
                                           End If
                                       Catch
                                       End Try
                                       rssiPanel.Controls.Clear()
                                       rssiLabels.Clear()
                                   End Sub
        topPanel.Controls.Add(btnReset)
        Dim bottomPanel As New FlowLayoutPanel With {
        .Location = New Point(10, 230),
        .Size = New Size(560, 40),
        .FlowDirection = FlowDirection.LeftToRight,
        .AutoSize = True
    }
        Me.Controls.Add(bottomPanel)
        btnEditList = New Button With {
        .Text = translations(currentLanguage)("btn_edit_list"),
        .AutoSize = True,
        .Padding = New Padding(10, 5, 10, 5)
    }
        AddHandler btnEditList.Click, AddressOf btnEditList_Click
        bottomPanel.Controls.Add(btnEditList)
        btnSave = New Button With {
        .Text = translations(currentLanguage)("btn_save"),
        .AutoSize = True,
        .Padding = New Padding(10, 5, 10, 5)
    }
        AddHandler btnSave.Click, AddressOf btnSave_Click
        bottomPanel.Controls.Add(btnSave)
        btnSearchRadioAgain = New Button With {
        .Text = translations(currentLanguage)("btn_search_radio_again"),
        .AutoSize = True,
        .Padding = New Padding(10, 5, 10, 5),
        .Visible = False
    }
        AddHandler btnSearchRadioAgain.Click, Sub()
                                                  AppendLog("btnSearchRadioAgain clicked")
                                                  If radioSearchCts IsNot Nothing Then
                                                      AppendLog("Cancelling previous search")
                                                      radioSearchCts.Cancel()
                                                      radioSearchCts.Dispose()
                                                  End If
                                                  radioSearchCts = New CancellationTokenSource()
                                                  AppendLog(translations(currentLanguage)("log_searching_radio_again"))
                                                  Task.Run(Sub() FindRadioIP(radioSearchCts.Token))
                                              End Sub
        bottomPanel.Controls.Add(btnSearchRadioAgain)
        btnSendToRadio = New Button With {
        .Text = translations(currentLanguage)("btn_send_to_radio"),
        .AutoSize = True,
        .Padding = New Padding(10, 5, 10, 5),
        .Visible = False
    }
        AddHandler btnSendToRadio.Click, AddressOf btnSendToRadio_Click
        bottomPanel.Controls.Add(btnSendToRadio)
        languageSelector = New ComboBox With {
        .AutoSize = True,
        .Location = New Point(10, Me.ClientSize.Height - 80),
        .DropDownStyle = ComboBoxStyle.DropDownList
    }
        Me.Controls.Add(languageSelector)
        languageSelector.Items.AddRange({"Polski", "English"})
        If languageSelector.Items.Count > 0 Then
            languageSelector.SelectedIndex = 0
        End If
        AddHandler languageSelector.SelectedIndexChanged, Sub()
                                                              AppendLog("Language selector changed to: " & languageSelector.SelectedItem.ToString())
                                                              currentLanguage = If(languageSelector.SelectedIndex = 0, "pl", "en")
                                                              UpdateUILanguage()
                                                          End Sub
        txtLog = New TextBox() With {
        .Location = New Point(10, 270),
        .Size = New Size(560, Me.ClientSize.Height - 350),
        .Multiline = True,
        .ScrollBars = ScrollBars.Vertical,
        .ReadOnly = True
    }
        Me.Controls.Add(txtLog)
        rssiPanel = New FlowLayoutPanel With {
    .Location = New Point(10, Me.ClientSize.Height - 80),
    .Size = New Size(560, 100), ' wysokość większa, żeby mieściły się wiersze
    .FlowDirection = FlowDirection.LeftToRight,
    .WrapContents = True,       ' <<< ważne! zawija do nowej linii
    .AutoScroll = True,         ' <<< jeśli będzie dużo, pojawi się scroll
    .AutoSize = False           ' <<< wyłącz, bo inaczej nadpisze wysokość
}
        Me.Controls.Add(rssiPanel)
        AppendLog(translations(currentLanguage)("log_searching_radio"))
        radioSearchCts = New CancellationTokenSource()
        ' Zastosuj nowoczesny motyw na wszystkich utworzonych kontrolkach
        ApplyTheme()
        LoadUrlCheckCache()   ' wczytaj zapamiętane wyniki sprawdzania (do 7 dni)
        ' Konfiguracja zapory przed rozpoczęciem komunikacji sieciowej
        Task.Run(Sub() ConfigureFirewallRules())
        Task.Run(Sub() FindRadioIP(radioSearchCts.Token))
        Task.Run(Sub() CheckForFileUpdateAsync())
        ' Wypakowuje cache i VLC z zasobów exe przy pierwszym uruchomieniu.
        ExtractCacheIfNeeded()           ' szybkie — tylko 2 pliki, synchronicznie
        Task.Run(Sub() ExtractVlcIfNeeded())  ' VLC ~45MB — w tle
    End Sub
    ' ====== Persystentny cache wyników sprawdzania URL ======

    Private Sub LoadUrlCheckCache()
        Try
            If Not File.Exists(UrlCheckCachePath) Then Return
            Dim cutoff As Long = DateTime.UtcNow.AddDays(-URL_CACHE_DAYS).Ticks
            Dim loaded As Integer = 0
            For Each line In File.ReadAllLines(UrlCheckCachePath, Encoding.UTF8)
                Dim p() As String = line.Split(vbTab)
                If p.Length < 3 Then Continue For
                Dim ticks As Long
                If Not Long.TryParse(p(2), ticks) OrElse ticks < cutoff Then Continue For
                stationCache(p(0)) = (p(1) = "1", p(2))
                loaded += 1
            Next
            If loaded > 0 Then AppendLog("[CACHE] Wczytano " & loaded & " wynikow URL (do " & URL_CACHE_DAYS & " dni)")
        Catch ex As Exception
            AppendLog("[CACHE] Blad wczytywania: " & ex.Message)
        End Try
    End Sub

    Friend Sub SaveUrlCheckCache()
        Try
            Dim now As String = DateTime.UtcNow.Ticks.ToString()
            Using sw As New StreamWriter(UrlCheckCachePath, False, Encoding.UTF8)
                For Each kvp In stationCache
                    If String.IsNullOrEmpty(kvp.Key) Then Continue For
                    ' Zapisujemy TYLKO pozytywne wyniki (working=True).
                    ' Negatywy (martwe stacje) nie są cachowane — przy następnym uruchomieniu
                    ' sprawdzamy je od nowa. Stacja która była down wczoraj może działać dziś.
                    If Not kvp.Value.Item1 Then Continue For
                    Dim ts = If(String.IsNullOrEmpty(kvp.Value.Item2), now, kvp.Value.Item2)
                    sw.WriteLine(kvp.Key & vbTab & "1" & vbTab & ts)
                Next
            End Using
        Catch
        End Try
    End Sub

    ' ====== Pomocniki wyglądu ======
    Private Sub StyleButton(b As Button)
        If b Is Nothing Then Return
        b.FlatStyle = FlatStyle.Flat
        b.FlatAppearance.BorderSize = 1
        b.FlatAppearance.BorderColor = ThemeBorder
        b.BackColor = Color.White
        b.ForeColor = ThemeText
        b.Font = New Font("Segoe UI", 9, FontStyle.Regular)
        b.Cursor = Cursors.Hand
        b.FlatAppearance.MouseOverBackColor = Color.FromArgb(232, 241, 251)
        b.FlatAppearance.MouseDownBackColor = Color.FromArgb(210, 228, 246)
    End Sub

    Private Sub StyleAccentButton(b As Button)
        If b Is Nothing Then Return
        b.FlatStyle = FlatStyle.Flat
        b.FlatAppearance.BorderSize = 0
        b.BackColor = ThemeAccent
        b.ForeColor = Color.White
        b.Font = New Font("Segoe UI", 9.5!, FontStyle.Bold)
        b.Cursor = Cursors.Hand
        b.FlatAppearance.MouseOverBackColor = ThemeAccentDark
        b.FlatAppearance.MouseDownBackColor = ThemeAccentDark
    End Sub

    Private Sub StyleGrid(g As DataGridView)
        If g Is Nothing Then Return
        g.EnableHeadersVisualStyles = False
        g.BackgroundColor = Color.White
        g.BorderStyle = BorderStyle.None
        g.GridColor = Color.FromArgb(230, 233, 237)
        g.ColumnHeadersDefaultCellStyle.BackColor = ThemeAccent
        g.ColumnHeadersDefaultCellStyle.ForeColor = Color.White
        g.ColumnHeadersDefaultCellStyle.SelectionBackColor = ThemeAccent
        g.ColumnHeadersDefaultCellStyle.Font = New Font("Segoe UI", 9.5!, FontStyle.Bold)
        g.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing
        g.ColumnHeadersHeight = 32
        g.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(244, 248, 252)
        g.DefaultCellStyle.SelectionBackColor = Color.FromArgb(205, 226, 246)
        g.DefaultCellStyle.SelectionForeColor = ThemeText
        g.DefaultCellStyle.ForeColor = ThemeText
        g.RowHeadersVisible = False
        g.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal
        g.AllowUserToResizeRows = False
    End Sub

    Private Sub ApplyTheme()
        Me.BackColor = ThemeBg
        Me.Font = New Font("Segoe UI", 9)
        For Each b As Button In {btnSelectFile, btnDownloadFromRadio, btnReset, btnEditList, btnSave, btnSearchRadioAgain, btnSendToRadio}
            StyleButton(b)
        Next
        ' Główna akcja – pobieranie stacji – wyróżniona kolorem akcentu.
        StyleAccentButton(btnSelectCountry)
        StyleGrid(dgvStations)
        If txtLog IsNot Nothing Then
            txtLog.BorderStyle = BorderStyle.FixedSingle
            txtLog.BackColor = Color.White
            txtLog.ForeColor = ThemeText
            txtLog.Font = New Font("Consolas", 9)
        End If
        If lblProgress IsNot Nothing Then
            lblProgress.ForeColor = ThemeText
            lblProgress.Font = New Font("Segoe UI", 9, FontStyle.Bold)
        End If
        If lblUpdateStatus IsNot Nothing Then lblUpdateStatus.ForeColor = ThemeAccentDark
        If progressBar IsNot Nothing Then progressBar.Style = ProgressBarStyle.Continuous
    End Sub

    Private Sub ConfigureFirewallRules()
        Try
            ' Pobierz ścieżkę do aplikacji
            Dim appPath As String = Application.ExecutablePath
            If String.IsNullOrEmpty(appPath) OrElse Not IO.File.Exists(appPath) Then
                Console.WriteLine("Błąd: Ścieżka do pliku wykonywalnego jest nieprawidłowa lub nie znaleziono pliku.")
                Return
            End If

            ' Przygotuj nazwę reguły zapory
            Dim appName As String = IO.Path.GetFileNameWithoutExtension(appPath) _
            .Replace(" ", "_").Replace("\", "_").Replace("/", "_").Replace(":", "_") _
            .Replace("*", "_").Replace("?", "_").Replace("""", "_").Replace("<", "_") _
            .Replace(">", "_").Replace("|", "_") & "_Full_Access"

            Console.WriteLine("Konfiguracja reguł zapory dla: " & appPath)
            Console.WriteLine("Nazwa reguły zapory: " & appName)

            ' Pobierz istniejące reguły + powiązane programy
            Dim checkRuleCommand As String =
            $"$rules = Get-NetFirewallRule -DisplayName '{appName}*' -ErrorAction SilentlyContinue; " &
            $"if ($rules) {{ foreach ($r in $rules) {{ $app = Get-NetFirewallApplicationFilter -AssociatedNetFirewallRule $r; " &
            $"Write-Output ($r.DisplayName + '|' + $app.Program) }} }}"

            Dim checkProcess As New Process()
            checkProcess.StartInfo.FileName = "powershell.exe"
            checkProcess.StartInfo.Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command ""{checkRuleCommand}"""
            checkProcess.StartInfo.UseShellExecute = False
            checkProcess.StartInfo.CreateNoWindow = True
            checkProcess.StartInfo.RedirectStandardOutput = True
            checkProcess.StartInfo.RedirectStandardError = True
            checkProcess.Start()
            Dim checkOutput As String = checkProcess.StandardOutput.ReadToEnd()
            checkProcess.WaitForExit()

            Dim needsUpdate As Boolean = True

            If Not String.IsNullOrWhiteSpace(checkOutput) Then
                ' sprawdzamy czy którakolwiek reguła ma tę samą ścieżkę
                If checkOutput.IndexOf(appPath, StringComparison.OrdinalIgnoreCase) >= 0 Then
                    Console.WriteLine("Reguły zapory są aktualne. Pomijanie tworzenia.")
                    needsUpdate = False
                Else
                    ' Usuń stare reguły, bo ścieżka się zmieniła
                    Dim removeRule As String = $"Get-NetFirewallRule -DisplayName '{appName}*' | Remove-NetFirewallRule"
                    Dim removeProcess As New Process()
                    removeProcess.StartInfo.FileName = "powershell.exe"
                    removeProcess.StartInfo.Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command ""{removeRule}"""
                    removeProcess.StartInfo.UseShellExecute = False
                    removeProcess.StartInfo.CreateNoWindow = True
                    removeProcess.StartInfo.RedirectStandardOutput = True
                    removeProcess.StartInfo.RedirectStandardError = True
                    removeProcess.Start()
                    removeProcess.WaitForExit()
                    Console.WriteLine("Stare reguły zapory usunięte.")
                End If
            End If

            If needsUpdate Then
                ' Escape ścieżki aplikacji dla PowerShell
                Dim escapedAppPath As String = "'" & appPath.Replace("'", "''") & "'"

                ' Polecenia PowerShell do dodania reguł
                Dim inboundRule As String = $"New-NetFirewallRule -DisplayName '{appName}_Inbound' -Direction Inbound -Program {escapedAppPath} -Action Allow -Profile Any"
                Dim outboundRule As String = $"New-NetFirewallRule -DisplayName '{appName}_Outbound' -Direction Outbound -Program {escapedAppPath} -Action Allow -Profile Any"

                ' Tworzenie reguły przychodzącej
                Dim inboundProcess As New Process()
                inboundProcess.StartInfo.FileName = "powershell.exe"
                inboundProcess.StartInfo.Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command ""{inboundRule}"""
                inboundProcess.StartInfo.UseShellExecute = False
                inboundProcess.StartInfo.CreateNoWindow = True
                inboundProcess.StartInfo.RedirectStandardOutput = True
                inboundProcess.StartInfo.RedirectStandardError = True
                inboundProcess.Start()
                Dim inboundError As String = inboundProcess.StandardError.ReadToEnd()
                inboundProcess.WaitForExit()

                If inboundProcess.ExitCode = 0 Then
                    Console.WriteLine("Reguła zapory dla ruchu przychodzącego dodana pomyślnie.")
                Else
                    Console.WriteLine("Błąd przy dodawaniu reguły przychodzącej: " & inboundError)
                End If

                ' Tworzenie reguły wychodzącej
                Dim outboundProcess As New Process()
                outboundProcess.StartInfo.FileName = "powershell.exe"
                outboundProcess.StartInfo.Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command ""{outboundRule}"""
                outboundProcess.StartInfo.UseShellExecute = False
                outboundProcess.StartInfo.CreateNoWindow = True
                outboundProcess.StartInfo.RedirectStandardOutput = True
                outboundProcess.StartInfo.RedirectStandardError = True
                outboundProcess.Start()
                Dim outboundError As String = outboundProcess.StandardError.ReadToEnd()
                outboundProcess.WaitForExit()

                If outboundProcess.ExitCode = 0 Then
                    Console.WriteLine("Reguła zapory dla ruchu wychodzącego dodana pomyślnie.")
                Else
                    Console.WriteLine("Błąd przy dodawaniu reguły wychodzącej: " & outboundError)
                End If
            End If

        Catch ex As Exception
            Console.WriteLine("Wyjątek w ConfigureFirewallRules: " & ex.Message)
        End Try
    End Sub


    Private Sub InitializeTranslations()
        translations = New Dictionary(Of String, Dictionary(Of String, String)) From {
        {"pl", New Dictionary(Of String, String) From {
            {"form_title", "Sprawdzanie listy stacji radiowych by SEBA99317"},
            {"btn_buy_coffee", "Postaw kawę ☕"},
            {"btn_search_radio_again", "Szukaj ponownie radia"},
            {"btn_select_file", "Wybierz plik"},
            {"btn_select_country", "Pobierz stacje"},
            {"btn_download_from_radio", "Pobierz listę z radia"},
            {"btn_reset", "Resetuj"},
            {"btn_edit_list", "Edytuj listę"},
            {"btn_save", "Zapisz listę"},
            {"btn_send_to_radio", "Znaleziono radio – Wyślij listę do radia"},
            {"btn_sending", "Wysyłanie..."},
            {"lbl_progress_initial", "Sprawdzono 0/0 stacji"},
            {"lbl_update_checking", "Sprawdzanie aktualizacji..."},
            {"lbl_update_available", "Aktualizacja dostępna! Pobierz nową wersję z GitHub."},
            {"lbl_update_none", "Brak dostępnych aktualizacji."},
            {"msg_cannot_open_link", "Nie można otworzyć linku: "},
            {"msg_error_title", "Błąd"},
            {"msg_no_radio_found", "Nie znaleziono radia w sieci."},
            {"msg_empty_playlist", "Plik playlist.csv jest pusty."},
            {"msg_no_valid_stations", "Nie znaleziono prawidłowych stacji w pliku playlist.csv."},
            {"msg_download_error", "Błąd pobierania playlist.csv: "},
            {"msg_general_error", "Błąd: "},
            {"msg_no_local_ip", "Nie można określić lokalnego adresu IP."},
            {"msg_no_radio_in_network", "Nie znaleziono radia w sieci lokalnej."},
            {"msg_radio_search_error", "Błąd wyszukiwania radia: "},
            {"msg_file_read_error", "Błąd wczytywania pliku: "},
            {"msg_no_valid_urls", "Nie znaleziono prawidłowych URL-i w pliku."},
            {"msg_country_fetch_error", "Błąd pobierania listy krajów: "},
            {"msg_stations_fetch_error", "Błąd pobierania stacji dla {0}: "},
            {"msg_no_stations_for_country", "Nie znaleziono stacji dla {0}."},
            {"msg_empty_stations_list", "Lista stacji jest pusta."},
            {"msg_file_save_error", "Błąd zapisu do pliku: "},
            {"msg_file_not_found", "Nie znaleziono pliku output_stations.csv na pulpicie."},
            {"msg_send_file_error", "Wystąpił błąd podczas wysyłania pliku."},
            {"msg_send_file_success", "Plik wysłany pomyślnie na: {0}"},
            {"msg_file_saved", "Lista zapisana do pliku: {0}"},
            {"msg_success_title", "Sukces"},
            {"msg_select_radio_title", "Wybierz radio"},
            {"msg_no_radio_selected", "Nie wybrano żadnego radia."},
            {"msg_update_check_error", "Błąd sprawdzania aktualizacji: {0}"},
            {"msg_file_not_in_repo", "Plik {0} nie znaleziony w repozytorium."},
            {"log_searching_radio", "🔍 Wyszukiwanie radia w sieci lokalnej..."},
            {"log_searching_radio_again", "🔄 Szukanie ponowne radia..."},
            {"log_radio_found", "✅ Znaleziono radio pod adresem: {0}"},
            {"log_radios_found", "📡 Znaleziono {0} radiów w sieci lokalnej."},
            {"log_downloading_playlist", "⬇️ Pobieranie playlist.csv..."},
            {"log_cleared_stations", "🗑️ Wyczyszczono bieżącą listę stacji."},
            {"log_invalid_line_format", "⚠️ Nieprawidłowy format w linii {0}: {1}"},
            {"log_invalid_url", "❌ Nieprawidłowy URL w linii {0}: {1}"},
            {"log_numeric_name", "🔢 Wykryto liczbę w nazwie w linii {0}, użyto nazwy z URL: {1}"},
            {"log_stations_loaded", "📥 Wczytano {0} stacji z playlist.csv. Sprawdzanie działających..."},
            {"log_stations_processed", "✅ Pozostawiono {0} działających stacji, usunięto {1} niedziałających."},
            {"log_reset_app", "♻️ Aplikacja została zresetowana."},
            {"log_processing_file", "📝 Przetwarzanie pliku..."},
            {"log_urls_loaded", "🔗 Wczytano {0} URL-i z pliku. Rozpoczynam sprawdzanie..."},
            {"log_country_selected", "🌍 Wybrano kraj: {0}"},
            {"log_country_cancelled", "🚫 Anulowano wybór kraju."},
            {"log_stations_fetched", "📥 Pobrano {0} stacji dla {1}. Sprawdzam działające..."},
            {"log_stations_added", "➕ Dodano wybrane stacje. Obecnie {0} stacji."},
            {"log_list_updated", "🔄 Lista stacji została zaktualizowana."},
            {"log_file_saved", "💾 Zapisano listę {0} stacji do pliku: {1}"},
            {"log_file_send_success", "📤 Plik output_stations.csv wysłany pomyślnie na {0}"},
            {"log_file_send_error", "❌ Błąd wysyłania pliku. Status: {0}"},
            {"log_station_check_error", "⚠️ Błąd sprawdzania {0} (próba {1}): {2}"},
            {"log_rssi_fetch_error", "📶 Błąd pobierania RSSI dla {0}: {1}"},
            {"log_rssi_updated", "📡 Zaktualizowano RSSI dla {0}: {1} dBm"},
            {"log_update_available", "⬆️ Dostępna nowa wersja pliku!"},
            {"log_update_none", "✅ Brak nowych aktualizacji pliku."},
            {"log_update_check_error", "⚠️ Błąd sprawdzania aktualizacji pliku: {0}"},
            {"log_file_not_in_repo", "❌ Plik {0} nie znaleziony w repozytorium."},
            {"file_filter", "Pliki tekstowe i CSV (*.txt;*.csv)|*.txt;*.csv|Wszystkie pliki (*.*)|*.*"},
            {"select_country_title", "Wybierz kraj"},
            {"select_stations_title", "Wybierz stacje do dodania"},
            {"btn_select_all", "Zaznacz wszystkie"},
            {"btn_recheck", "Sprawdz ponownie"},
            {"btn_rechecking", "Sprawdzam..."},
            {"btn_close", "Zamknij"},
            {"btn_play", ChrW(&H25B6) & " Odtworz"},
            {"lbl_added_total", "Dodano {0} stacji (lacznie: {1})"},
            {"lbl_checking", "Sprawdzanie..."},
            {"lbl_check_done", "Gotowe: {0} dzialajacych stacji"},
            {"lbl_found_working", "Stacji na liście: {0}"},
            {"lbl_filter", "Filtr / szukaj:"},
            {"search_title", "Wyszukaj stacje"},
            {"search_prompt", "Wpisz nazwę stacji, aby wyszukać we wszystkich źródłach (Radio Browser, yoRadio, SomaFM)." & vbCrLf & "Zostaw puste pole, aby pobrać wszystko na raz."},
            {"src_all", "wszystkie źródła"},
            {"log_fetching_all", "🌐 Pobieranie list ze wszystkich źródeł..."},
            {"log_cache_loaded", "[CACHE] Wczytano {0} stacji z cache (< 24h). Kliknij Resetuj by odswiezye."},
            {"msg_cache_ask_title", "Znaleziono zapisane stacje"},
            {"msg_cache_ask", "Masz {0} sprawdzonych stacji z {1}." & vbCrLf & vbCrLf & "TAK  - otworz od razu (szybko)" & vbCrLf & "NIE  - pobierz swieza liste ze wszystkich zrodel (wolniej)"},
            {"log_cache_expired", "[CACHE] Starszy niz 24h – pobieram swieze dane..."},
            {"log_cache_saved", "[CACHE] Zapisano {0} stacji: {1}"},
            {"log_source_count", "   • {0}: {1} stacji"},
            {"log_source_skipped", "   ⚠️ {0} pominięto: {1}"},
            {"log_capped", "ℹ️ Pobrano {0} stacji – ograniczam do {1} przed sprawdzaniem."},
            {"log_dedup", "Pobrano {0} -> po dedup+filtr: {1} (usunieto {2})"},
            {"edit_list_title", "Edytuj listę stacji"},
            {"col_station_name", "Nazwa stacji"},
            {"col_station_url", "Adres URL"},
            {"txt_new_station_name", "Nazwa stacji"},
            {"txt_new_station_url", "Adres URL"},
            {"btn_ok", "OK"},
            {"btn_add", "Dodaj"},
            {"btn_save_changes", "Zapisz zmiany"},
            {"btn_cancel", "Anuluj"},
            {"station_check_success", "Działa (kod: {0}, typ: {1}, próba {2})"},
            {"station_check_failure", "Nie działa (kod: {0}, typ: {1}, próba {2})"},
            {"station_check_no_response", "Niedziała po {0} próbach"}
        }},
        {"en", New Dictionary(Of String, String) From {
            {"form_title", "Radio Station List Checker by SEBA99317"},
            {"btn_buy_coffee", "Buy me a coffee ☕"},
            {"btn_search_radio_again", "Search Radio Again"},
            {"btn_select_file", "Select File"},
            {"btn_select_country", "Download Stations"},
            {"btn_download_from_radio", "Download List from Radio"},
            {"btn_reset", "Reset"},
            {"btn_edit_list", "Edit List"},
            {"btn_save", "Save List"},
            {"btn_send_to_radio", "Radio Found – Send List to Radio"},
            {"btn_sending", "Sending..."},
            {"lbl_progress_initial", "Checked 0/0 stations"},
            {"lbl_update_checking", "Checking for updates..."},
            {"lbl_update_available", "Update available! Download the new version from GitHub."},
            {"lbl_update_none", "No updates available."},
            {"msg_cannot_open_link", "Cannot open link: "},
            {"msg_error_title", "Error"},
            {"msg_no_radio_found", "No radio found on the network."},
            {"msg_empty_playlist", "The playlist.csv file is empty."},
            {"msg_no_valid_stations", "No valid stations found in playlist.csv."},
            {"msg_download_error", "Error downloading playlist.csv: "},
            {"msg_general_error", "Error: "},
            {"msg_no_local_ip", "Cannot determine local IP address."},
            {"msg_no_radio_in_network", "No radio found on the local network."},
            {"msg_radio_search_error", "Error searching for radio: "},
            {"msg_file_read_error", "Error reading file: "},
            {"msg_no_valid_urls", "No valid URLs found in the file."},
            {"msg_country_fetch_error", "Error fetching country list: "},
            {"msg_stations_fetch_error", "Error fetching stations for {0}: "},
            {"msg_no_stations_for_country", "No stations found for {0}."},
            {"msg_empty_stations_list", "The station list is empty."},
            {"msg_file_save_error", "Error saving to file: "},
            {"msg_file_not_found", "The output_stations.csv file was not found on the desktop."},
            {"msg_send_file_error", "An error occurred while sending the file."},
            {"msg_send_file_success", "File sent successfully to: {0}"},
            {"msg_file_saved", "List saved to file: {0}"},
            {"msg_success_title", "Success"},
            {"msg_select_radio_title", "Select Radio"},
            {"msg_no_radio_selected", "No radio selected."},
            {"msg_update_check_error", "Error checking for updates: {0}"},
            {"msg_file_not_in_repo", "File {0} not found in the repository."},
            {"log_searching_radio", "🔍 Searching for radio on the local network..."},
            {"log_searching_radio_again", "🔄 Searching for radio again..."},
            {"log_radio_found", "✅ Radio found at address: {0}"},
            {"log_radios_found", "📡 Found {0} radios on the local network."},
            {"log_downloading_playlist", "⬇️ Downloading playlist.csv..."},
            {"log_cleared_stations", "🗑️ Cleared the current station list."},
            {"log_invalid_line_format", "⚠️ Invalid format in line {0}: {1}"},
            {"log_invalid_url", "❌ Invalid URL in line {0}: {1}"},
            {"log_numeric_name", "🔢 Detected number in name at line {0}, used name from URL: {1}"},
            {"log_stations_loaded", "📥 Loaded {0} stations from playlist.csv. Checking active ones..."},
            {"log_stations_processed", "✅ Kept {0} active stations, removed {1} inactive ones."},
            {"log_reset_app", "♻️ Application has been reset."},
            {"log_processing_file", "📝 Processing file..."},
            {"log_urls_loaded", "🔗 Loaded {0} URLs from file. Starting check..."},
            {"log_country_selected", "🌍 Selected country: {0}"},
            {"log_country_cancelled", "🚫 Country selection cancelled."},
            {"log_stations_fetched", "📥 Fetched {0} stations for {1}. Checking active ones..."},
            {"log_stations_added", "➕ Added selected stations. Now {0} stations."},
            {"log_list_updated", "🔄 Station list updated."},
            {"log_file_saved", "💾 Saved {0} stations to file: {1}"},
            {"log_file_send_success", "📤 File output_stations.csv sent successfully to {0}"},
            {"log_file_send_error", "❌ Error sending file. Status: {0}"},
            {"log_station_check_error", "⚠️ Error checking {0} (attempt {1}): {2}"},
            {"log_rssi_fetch_error", "📶 Error fetching RSSI for {0}: {1}"},
            {"log_rssi_updated", "📡 Updated RSSI for {0}: {1} dBm"},
            {"log_update_available", "⬆️ A new version of the file is available!"},
            {"log_update_none", "✅ No new updates for the file."},
            {"log_update_check_error", "⚠️ Error checking for file updates: {0}"},
            {"log_file_not_in_repo", "❌ File {0} not found in the repository."},
            {"file_filter", "Pliki tekstowe i CSV (*.txt;*.csv)|*.txt;*.csv|Wszystkie pliki (*.*)|*.*"},
            {"select_country_title", "Select Country"},
            {"select_stations_title", "Select Stations to Add"},
            {"btn_select_all", "Select All"},
            {"btn_recheck", "Check Again"},
            {"btn_rechecking", "Checking..."},
            {"btn_close", "Close"},
            {"btn_play", ChrW(&H25B6) & " Play"},
            {"lbl_added_total", "Added {0} stations (total: {1})"},
            {"lbl_checking", "Checking..."},
            {"lbl_check_done", "Done: {0} working stations"},
            {"lbl_found_working", "Stations in list: {0}"},
            {"lbl_filter", "Filter / search:"},
            {"search_title", "Search stations"},
            {"search_prompt", "Type a station name to search across all sources (Radio Browser, yoRadio, SomaFM)." & vbCrLf & "Leave empty to fetch everything at once."},
            {"src_all", "all sources"},
            {"log_fetching_all", "🌐 Fetching lists from all sources..."},
            {"log_cache_loaded", "[CACHE] Loaded {0} stations from cache (< 24h). Click Reset to refresh."},
            {"msg_cache_ask_title", "Saved stations found"},
            {"msg_cache_ask", "You have {0} checked stations from {1}." & vbCrLf & vbCrLf & "YES  - open immediately (fast)" & vbCrLf & "NO   - fetch fresh list from all sources (slower)"},
            {"log_cache_expired", "[CACHE] Older than 24h - fetching fresh data..."},
            {"log_cache_saved", "[CACHE] Saved {0} stations: {1}"},
            {"log_source_count", "   • {0}: {1} stations"},
            {"log_source_skipped", "   ⚠️ {0} skipped: {1}"},
            {"log_capped", "ℹ️ Fetched {0} stations – limiting to {1} before checking."},
            {"log_dedup", "Fetched {0} -> after dedup+filter: {1} (removed {2})"},
            {"edit_list_title", "Edit Station List"},
            {"col_station_name", "Station Name"},
            {"col_station_url", "URL"},
            {"txt_new_station_name", "Station Name"},
            {"txt_new_station_url", "URL"},
            {"btn_ok", "OK"},
            {"btn_add", "Add"},
            {"btn_save_changes", "Save Changes"},
            {"btn_cancel", "Cancel"},
            {"station_check_success", "Working (code: {0}, type: {1}, attempt {2})"},
            {"station_check_failure", "Not working (code: {0}, type: {1}, attempt {2})"},
            {"station_check_no_response", "Not working after {0} attempts"}
        }}
    }
    End Sub

    Private Async Function CheckForFileUpdateAsync() As Task
        Dim errorMessage As String = Nothing
        Try
            Using client As New HttpClient()
                client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0")
                Dim apiUrl As String = $"https://api.github.com/repos/{REPO_PATH}/commits?path={FILE_TO_CHECK}"
                Dim response = Await client.GetAsync(apiUrl)
                If Not response.IsSuccessStatusCode Then
                    errorMessage = $"HTTP error {response.StatusCode}: {response.ReasonPhrase}"
                    AppendLog($"HTTP error: {errorMessage}")
                    Return
                End If
                Dim jsonData = Await response.Content.ReadAsStringAsync()
                Dim jsonDoc = JsonDocument.Parse(jsonData)
                If jsonDoc.RootElement.GetArrayLength() = 0 Then
                    Me.Invoke(Sub()
                                  lblUpdateStatus.Visible = False
                                  AppendLog(String.Format(translations(currentLanguage)("log_file_not_in_repo"), FILE_TO_CHECK))
                              End Sub)
                    Return
                End If
                Dim latestCommitDate As DateTime = jsonDoc.RootElement(0).GetProperty("commit").GetProperty("committer").GetProperty("date").GetDateTime()
                Dim localFilePath As String = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, FILE_TO_CHECK)
                Dim localFileDate As DateTime = DateTime.MinValue
                If File.Exists(localFilePath) Then
                    localFileDate = File.GetLastWriteTimeUtc(localFilePath)
                Else
                    AppendLog($"Local file {FILE_TO_CHECK} not found at {localFilePath}")
                End If
                If latestCommitDate > localFileDate Then
                    Me.Invoke(Sub()
                                  lblUpdateStatus.Text = translations(currentLanguage)("lbl_update_available")
                                  lblUpdateStatus.ForeColor = Color.Green
                                  lblUpdateStatus.Cursor = Cursors.Hand
                                  lblUpdateStatus.Visible = True
                                  RemoveHandler lblUpdateStatus.Click, AddressOf UpdateStatus_Click
                                  AddHandler lblUpdateStatus.Click, AddressOf UpdateStatus_Click
                                  AppendLog(translations(currentLanguage)("log_update_available"))
                                  lblUpdateStatus.Location = New Point(Me.ClientSize.Width - lblUpdateStatus.Width, 0)
                                  lblUpdateStatus.BringToFront()
                              End Sub)
                Else
                    Me.Invoke(Sub()
                                  lblUpdateStatus.Visible = False
                                  AppendLog(translations(currentLanguage)("log_update_none"))
                              End Sub)
                End If
            End Using
        Catch ex As Exception
            errorMessage = ex.Message
            AppendLog($"Exception in CheckForFileUpdateAsync: {errorMessage}")
        End Try
        If errorMessage IsNot Nothing Then
            Me.Invoke(Sub()
                          lblUpdateStatus.Text = String.Format(translations(currentLanguage)("msg_update_check_error"), errorMessage)
                          lblUpdateStatus.ForeColor = Color.Red
                          lblUpdateStatus.Cursor = Cursors.Default
                          lblUpdateStatus.Visible = True
                          AppendLog(String.Format(translations(currentLanguage)("log_update_check_error"), errorMessage))
                          lblUpdateStatus.Location = New Point(Me.ClientSize.Width - lblUpdateStatus.Width, 0)
                          lblUpdateStatus.BringToFront()
                      End Sub)
        End If
    End Function
    Private Sub UpdateStatus_Click(sender As Object, e As EventArgs)
        Try
            System.Diagnostics.Process.Start(New ProcessStartInfo With {
                .FileName = $"https://github.com/{REPO_PATH}/raw/refs/heads/main/{FILE_TO_CHECK}",
                .UseShellExecute = True
            })
        Catch ex As Exception
            MessageBox.Show(String.Format(translations(currentLanguage)("msg_cannot_open_link"), ex.Message), translations(currentLanguage)("msg_error_title"), MessageBoxButtons.OK, MessageBoxIcon.Error)
        End Try
    End Sub
    Private Sub UpdateUILanguage()
        Me.Text = translations(currentLanguage)("form_title")
        btnBuyCoffee.Text = translations(currentLanguage)("btn_buy_coffee")
        btnSelectFile.Text = translations(currentLanguage)("btn_select_file")
        btnSelectCountry.Text = translations(currentLanguage)("btn_select_country")
        btnDownloadFromRadio.Text = translations(currentLanguage)("btn_download_from_radio")
        btnReset.Text = translations(currentLanguage)("btn_reset")
        btnEditList.Text = translations(currentLanguage)("btn_edit_list")
        btnSave.Text = translations(currentLanguage)("btn_save")
        If Not isRadioSearchRunning Then
            btnSearchRadioAgain.Text = translations(currentLanguage)("btn_search_radio_again")
            btnSendToRadio.Text = If(btnSendToRadio.Text = translations("pl")("btn_sending") OrElse btnSendToRadio.Text = translations("en")("btn_sending"),
                                     translations(currentLanguage)("btn_sending"),
                                     translations(currentLanguage)("btn_send_to_radio"))
        End If
        If lblUpdateStatus.Visible Then
            lblUpdateStatus.Text = If(lblUpdateStatus.Text.Contains("Aktualizacja dostępna") OrElse lblUpdateStatus.Text.Contains("Update available"),
                                      translations(currentLanguage)("lbl_update_available"),
                                      String.Format(translations(currentLanguage)("msg_update_check_error"), lblUpdateStatus.Text.Substring(translations(currentLanguage)("msg_update_check_error").IndexOf("{0}"))))
            lblUpdateStatus.Location = New Point(Me.ClientSize.Width - lblUpdateStatus.Width, 0)
            lblUpdateStatus.BringToFront()
        End If
        If checkedStationsCount > 0 AndAlso totalStationsCount > 0 Then
            lblProgress.Text = String.Format(translations(currentLanguage)("lbl_progress_initial").Replace("0/0", "{0}/{1}"), checkedStationsCount, totalStationsCount)
        Else
            lblProgress.Text = translations(currentLanguage)("lbl_progress_initial")
        End If
        btnBuyCoffee.Width = TextRenderer.MeasureText(btnBuyCoffee.Text, btnBuyCoffee.Font).Width + 20
        btnSelectFile.Width = TextRenderer.MeasureText(btnSelectFile.Text, btnSelectFile.Font).Width + 20
        btnSelectCountry.Width = TextRenderer.MeasureText(btnSelectCountry.Text, btnSelectCountry.Font).Width + 20
        btnDownloadFromRadio.Width = TextRenderer.MeasureText(btnDownloadFromRadio.Text, btnDownloadFromRadio.Font).Width + 20
        btnReset.Width = TextRenderer.MeasureText(btnReset.Text, btnReset.Font).Width + 20
        btnEditList.Width = TextRenderer.MeasureText(btnEditList.Text, btnEditList.Font).Width + 20
        btnSave.Width = TextRenderer.MeasureText(btnSave.Text, btnSave.Font).Width + 20
        If Not isRadioSearchRunning Then
            btnSearchRadioAgain.Width = TextRenderer.MeasureText(btnSearchRadioAgain.Text, btnSearchRadioAgain.Font).Width + 20
            btnSendToRadio.Width = TextRenderer.MeasureText(btnSendToRadio.Text, btnSendToRadio.Font).Width + 20
        End If
        dgvStations.Columns(0).HeaderText = translations(currentLanguage)("col_station_name")
        dgvStations.Columns(1).HeaderText = translations(currentLanguage)("col_station_url")
        For Each kvp In rssiLabels
            Dim ip = kvp.Key
            Dim rssi = If(kvp.Value.Tag IsNot Nothing, kvp.Value.Tag.ToString(), "N/A")
            kvp.Value.Text = $"{ip}: {rssi} dBm"
        Next
    End Sub
    Private Sub UpdateRadioButtonsVisibility(searchVisible As Boolean, sendVisible As Boolean, downloadVisible As Boolean)
        btnSearchRadioAgain.Visible = searchVisible
        btnSendToRadio.Visible = sendVisible
        btnDownloadFromRadio.Visible = downloadVisible
        UpdateUILanguage()
    End Sub
    Private Async Sub FindRadioIP(ct As CancellationToken)
        If isRadioSearchRunning Then Return
        isRadioSearchRunning = True
        Dim searchRadioAgainVisible As Boolean = False
        Dim sendToRadioVisible As Boolean = False
        Dim downloadFromRadioVisible As Boolean = False
        Try
            Dim localIP As String = GetLocalIPAddress()
            AppendLog(localIP)
            If String.IsNullOrEmpty(localIP) Then
                AppendLog(GetTranslation("msg_no_local_ip", "❌ Brak adresu lokalnego"))
                searchRadioAgainVisible = True
                Me.Invoke(Sub() UpdateRadioButtonsVisibility(searchRadioAgainVisible, sendToRadioVisible, downloadFromRadioVisible))
                Return
            End If
            Dim baseIP As String = localIP.Substring(0, localIP.LastIndexOf(".") + 1)
            radioIPs.Clear()
            Me.Invoke(Sub() rssiPanel.Controls.Clear())
            rssiLabels.Clear()
            'AppendLog(GetTranslation("log_searching_radio", "🔎 Skanowanie sieci..."))
            Dim tasks As New List(Of Task)
            Dim handler As New HttpClientHandler() With {.AllowAutoRedirect = False}
            Using client As New HttpClient(handler)
                client.Timeout = TimeSpan.FromMilliseconds(1000)
                Dim semaphore As New SemaphoreSlim(100)
                For i As Integer = 1 To 254
                    Dim ip As String = $"{baseIP}{i}"
                    Dim urls As String() = {$"http://{ip}", $"http://{ip}/settings.html"}
                    For Each url In urls
                        tasks.Add(Task.Run(Async Function()
                                               Await semaphore.WaitAsync()
                                               Try
                                                   Dim response As HttpResponseMessage = Await client.GetAsync(url, ct)
                                                   Dim html As String = Await response.Content.ReadAsStringAsync()
                                                   If html.ToLower().Contains("yoradio") OrElse html.ToLower().Contains("radio") OrElse html.ToLower().Contains("player") Then
                                                       SyncLock radioIPs
                                                           If Not radioIPs.Contains(ip) Then
                                                               radioIPs.Add(ip)
                                                               AppendLog(String.Format(GetTranslation("log_radio_found", "✅ Radio znalezione: {0}"), ip))
                                                           End If
                                                       End SyncLock
                                                   End If
                                               Catch ex As OperationCanceledException
                                                   ' Ignoruj, jeśli zadanie zostało anulowane
                                               Catch
                                                   ' Ignoruj inne błędy
                                               Finally
                                                   semaphore.Release()
                                               End Try
                                           End Function, ct))
                    Next
                Next
                Await Task.WhenAll(tasks)
            End Using
            If radioIPs.Count > 0 Then
                sendToRadioVisible = True
                downloadFromRadioVisible = True
                AppendLog(String.Format(GetTranslation("log_radios_found", "📡 Znaleziono {0} radia."), radioIPs.Count))
                Me.Invoke(Sub() UpdateRadioButtonsVisibility(False, sendToRadioVisible, downloadFromRadioVisible))
                Await FetchAndDisplayRssi(radioIPs, ct)
            Else
                AppendLog(GetTranslation("msg_no_radio_in_network", "❌ Nie znaleziono radia."))
                searchRadioAgainVisible = True
                Me.Invoke(Sub() UpdateRadioButtonsVisibility(searchRadioAgainVisible, sendToRadioVisible, downloadFromRadioVisible))
            End If
        Catch ex As OperationCanceledException
            AppendLog(GetTranslation("log_country_cancelled", "Wyszukiwanie radia anulowane."))
        Catch ex As Exception
            AppendLog(String.Format(GetTranslation("msg_radio_search_error", "Błąd wyszukiwania radia: {0}"), ex.Message))
        Finally
            isRadioSearchRunning = False
        End Try
    End Sub

    Private Function GetTranslation(key As String, defaultValue As String) As String
        If translations IsNot Nothing AndAlso translations.ContainsKey(currentLanguage) AndAlso translations(currentLanguage).ContainsKey(key) Then
            Return translations(currentLanguage)(key)
        Else
            Return defaultValue
        End If
    End Function



    Private Async Function FetchAndDisplayRssi(ips As List(Of String), ct As CancellationToken) As Task
        rssiPanel.Controls.Clear()
        rssiLabels.Clear()

        ' Funkcja pomocnicza – konwersja RSSI na poziom sygnału 0-5
        Dim RssiToSignalLevel As Func(Of Integer, Integer) = Function(rssi)
                                                                 If rssi >= -50 Then
                                                                     Return 5
                                                                 ElseIf rssi >= -60 Then
                                                                     Return 4
                                                                 ElseIf rssi >= -70 Then
                                                                     Return 3
                                                                 ElseIf rssi >= -80 Then
                                                                     Return 2
                                                                 ElseIf rssi >= -90 Then
                                                                     Return 1
                                                                 Else
                                                                     Return 0
                                                                 End If
                                                             End Function
        ' Dodajemy labelki do panelu w układzie siatki
        Dim colCount As Integer = 2   ' liczba kolumn
        Dim spacingX As Integer = 220 ' odstęp poziomy
        Dim spacingY As Integer = 40  ' odstęp pionowy
        Dim startX As Integer = 10
        Dim startY As Integer = 10

        Dim index As Integer = 0
        For Each ip In ips
            Dim row As Integer = index \ colCount
            Dim col As Integer = index Mod colCount

            Dim label As New Label With {
        .Text = ip & ": brak sygnalu",
        .AutoSize = True,                  ' WAŻNE – wyłączamy AutoSize          
        .Height = 30,                       ' wysokość etykiety
        .Font = New Font("Segoe UI", 10),
        .TextAlign = ContentAlignment.MiddleLeft,
        .Location = New Point(startX + col * spacingX, startY + row * spacingY)
    }

            rssiLabels.Add(ip, label)
            Me.Invoke(Sub() rssiPanel.Controls.Add(label))

            index += 1
        Next

        ' Tworzymy taski dla wszystkich IP
        Dim tasks = ips.Select(Function(ip) Task.Run(Async Function()
                                                         While Not ct.IsCancellationRequested
                                                             Try
                                                                 Using ws As New ClientWebSocket()
                                                                     Await ws.ConnectAsync(New Uri($"ws://{ip}/ws"), ct)

                                                                     Dim buffer(1023) As Byte

                                                                     While ws.State = WebSocketState.Open AndAlso Not ct.IsCancellationRequested
                                                                         Dim result = Await ws.ReceiveAsync(New ArraySegment(Of Byte)(buffer), ct)
                                                                         If result.MessageType = WebSocketMessageType.Text Then
                                                                             Dim message = Encoding.UTF8.GetString(buffer, 0, result.Count)
                                                                             Try
                                                                                 Dim jsonDoc = JsonDocument.Parse(message)
                                                                                 If jsonDoc.RootElement.TryGetProperty("rssi", Nothing) Then
                                                                                     Dim rssi = jsonDoc.RootElement.GetProperty("rssi").GetInt32()
                                                                                     Dim level = RssiToSignalLevel(rssi)
                                                                                     ' Generowanie symboli sygnału
                                                                                     Dim bars As String = ChrW(&H2582) & ChrW(&H2583) & ChrW(&H2584) & ChrW(&H2585) & ChrW(&H2586) & ChrW(&H2587)
                                                                                     Dim signalBars As String = If(level = 0, "X", New String(bars.Take(level).ToArray()))
                                                                                     Me.Invoke(Sub()
                                                                                                   If rssiLabels.ContainsKey(ip) Then
                                                                                                       rssiLabels(ip).Text = ip & " Wi-Fi " & signalBars & " (" & rssi & " dBm)"
                                                                                                       rssiLabels(ip).Tag = rssi
                                                                                                   End If
                                                                                               End Sub)
                                                                                 End If
                                                                             Catch ex As JsonException
                                                                             End Try
                                                                         End If
                                                                     End While
                                                                 End Using
                                                             Catch ex As WebSocketException
                                                                 ' Jeśli utracono połączenie, ustawiamy brak sygnału
                                                                 Me.Invoke(Sub()
                                                                               If rssiLabels.ContainsKey(ip) Then
                                                                                   rssiLabels(ip).Text = ip & ": brak sygnalu"
                                                                                   rssiLabels(ip).Tag = Nothing
                                                                               End If
                                                                           End Sub)
                                                             Catch ex As OperationCanceledException
                                                                 Exit While
                                                             Catch ex As Exception
                                                                 ' Możesz zalogować inne błędy jeśli chcesz
                                                             End Try

                                                             ' Odczekaj chwilę przed ponowną próbą połączenia
                                                             If Not ct.IsCancellationRequested Then
                                                                 Await Task.Delay(2000, ct) ' np. 2 sekundy przerwy
                                                             End If
                                                         End While
                                                     End Function)).ToList()

        Await Task.WhenAll(tasks)
    End Function


    Private Function ShowRadioSelectionDialog(action As String) As String
        Dim selForm As New Form With {
            .Text = translations(currentLanguage)("msg_select_radio_title"),
            .Size = New Size(300, 200),
            .FormBorderStyle = FormBorderStyle.FixedDialog,
            .MaximizeBox = False,
            .MinimizeBox = False,
            .StartPosition = FormStartPosition.CenterParent
        }
        Dim lb As New ListBox With {.Dock = DockStyle.Fill}
        lb.Items.AddRange(radioIPs.Select(Function(ip) $"{ip} ({If(rssiLabels.ContainsKey(ip) AndAlso rssiLabels(ip).Tag IsNot Nothing, rssiLabels(ip).Tag & " dBm", "N/A")})").ToArray())
        selForm.Controls.Add(lb)
        Dim btnOk As New Button With {.Text = translations(currentLanguage)("btn_ok"), .Dock = DockStyle.Bottom, .Height = 30}
        AddHandler btnOk.Click, Sub()
                                    selForm.DialogResult = DialogResult.OK
                                    selForm.Close()
                                End Sub
        selForm.Controls.Add(btnOk)
        If selForm.ShowDialog() = DialogResult.OK AndAlso lb.SelectedIndex >= 0 Then
            Return radioIPs(lb.SelectedIndex)
        Else
            MessageBox.Show(translations(currentLanguage)("msg_no_radio_selected"), translations(currentLanguage)("msg_error_title"), MessageBoxButtons.OK, MessageBoxIcon.Error)
            Return Nothing
        End If
    End Function
    Private Async Sub btnDownloadFromRadio_Click(sender As Object, e As EventArgs)
        Dim selectedIP As String
        If radioIPs.Count = 0 Then
            MessageBox.Show(translations(currentLanguage)("msg_no_radio_found"), translations(currentLanguage)("msg_error_title"), MessageBoxButtons.OK, MessageBoxIcon.Error)
            AppendLog(translations(currentLanguage)("msg_no_radio_found"))
            Return
        ElseIf radioIPs.Count = 1 Then
            selectedIP = radioIPs(0)
        Else
            selectedIP = ShowRadioSelectionDialog("download")
            If selectedIP Is Nothing Then Return
        End If
        Dim playlistUrl As String = $"http://{selectedIP}/data/playlist.csv"
        AppendLog(translations(currentLanguage)("log_downloading_playlist"))

        Try
            Using client As New HttpClient()
                client.Timeout = TimeSpan.FromSeconds(10)
                Dim response As HttpResponseMessage = Await client.GetAsync(playlistUrl & "?" & DateTime.Now.Ticks)
                response.EnsureSuccessStatusCode()
                Dim csvContent As String = Await response.Content.ReadAsStringAsync()
                If String.IsNullOrEmpty(csvContent) Then
                    MessageBox.Show(translations(currentLanguage)("msg_empty_playlist"), translations(currentLanguage)("msg_error_title"), MessageBoxButtons.OK, MessageBoxIcon.Error)
                    AppendLog(translations(currentLanguage)("msg_empty_playlist"))
                    Return
                End If
                stations.Clear()
                dgvStations.Rows.Clear()
                progressBar.Value = 0
                checkedStationsCount = 0
                totalStationsCount = 0
                lblProgress.Text = translations(currentLanguage)("lbl_progress_initial")
                AppendLog(translations(currentLanguage)("log_cleared_stations"))
                Dim lines = csvContent.Split({Environment.NewLine, vbLf}, StringSplitOptions.RemoveEmptyEntries)
                For i As Integer = 0 To lines.Length - 1
                    Dim line As String = lines(i).Trim()
                    If String.IsNullOrEmpty(line) Then Continue For
                    Dim parts() As String = line.Split(vbTab)
                    Dim name As String
                    Dim url As String
                    Dim volume As String = "0"
                    If parts.Length >= 3 Then
                        name = CleanStationName(parts(0).Trim())
                        url = parts(1).Trim()
                        volume = parts(2).Trim()
                    ElseIf parts.Length = 2 Then
                        name = CleanStationName(parts(0).Trim())
                        url = parts(1).Trim()
                    Else
                        AppendLog(String.Format(translations(currentLanguage)("log_invalid_line_format"), i + 1, line))
                        Continue For
                    End If
                    If Not Regex.IsMatch(url, "^https?://[^\s]+$") Then
                        AppendLog(String.Format(translations(currentLanguage)("log_invalid_url"), i + 1, url))
                        Continue For
                    End If
                    If Integer.TryParse(name, 0) Then
                        name = DeriveNameFromUrl(url)
                        AppendLog(String.Format(translations(currentLanguage)("log_numeric_name"), i + 1, name))
                    End If
                    stations.Add(New Station With {
                        .Name = name,
                        .URL = url,
                        .Volume = volume
                    })
                Next
                If stations.Count = 0 Then
                    MessageBox.Show(translations(currentLanguage)("msg_no_valid_stations"), translations(currentLanguage)("msg_error_title"), MessageBoxButtons.OK, MessageBoxIcon.Information)
                    AppendLog(translations(currentLanguage)("msg_no_valid_stations"))
                    Return
                End If
                totalStationsCount = stations.Count
                AppendLog(String.Format(translations(currentLanguage)("log_stations_loaded"), totalStationsCount))
                lblProgress.Text = String.Format(translations(currentLanguage)("lbl_progress_initial").Replace("0/0", "0/{0}"), totalStationsCount)
                Dim totalBefore = stations.Count
                Dim working = Await CheckStationsAsync(stations)
                stations = working
                UpdateDataGridView()
                AppendLog(String.Format(translations(currentLanguage)("log_stations_processed"), stations.Count, totalBefore - stations.Count))
            End Using
        Catch ex As HttpRequestException
            MessageBox.Show(String.Format(translations(currentLanguage)("msg_download_error"), ex.Message), translations(currentLanguage)("msg_error_title"), MessageBoxButtons.OK, MessageBoxIcon.Error)
            AppendLog(String.Format(translations(currentLanguage)("msg_download_error"), ex.Message))
        Catch ex As Exception
            MessageBox.Show(String.Format(translations(currentLanguage)("msg_general_error"), ex.Message), translations(currentLanguage)("msg_error_title"), MessageBoxButtons.OK, MessageBoxIcon.Error)
            AppendLog(String.Format(translations(currentLanguage)("msg_general_error"), ex.Message))
        End Try
    End Sub
    Private Async Sub btnSendToRadio_Click(sender As Object, e As EventArgs)
        Dim selectedIP As String
        If radioIPs.Count = 0 Then
            MessageBox.Show(translations(currentLanguage)("msg_no_radio_found"), translations(currentLanguage)("msg_error_title"), MessageBoxButtons.OK, MessageBoxIcon.Error)
            Return
        ElseIf radioIPs.Count = 1 Then
            selectedIP = radioIPs(0)
        Else
            selectedIP = ShowRadioSelectionDialog("send")
            If selectedIP Is Nothing Then Return
        End If
        Dim outputFile As String = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "output_stations.csv")
        Try
            Using writer As New StreamWriter(outputFile, False)
                For Each st In stations
                    writer.WriteLine($"{st.Name}{vbTab}{st.URL}{vbTab}{st.Volume}")
                Next
            End Using
            AppendLog(String.Format(translations(currentLanguage)("log_file_saved"), stations.Count, outputFile))
        Catch ex As Exception
            MessageBox.Show(String.Format(translations(currentLanguage)("msg_file_save_error"), ex.Message), translations(currentLanguage)("msg_error_title"), MessageBoxButtons.OK, MessageBoxIcon.Error)
            Return
        End Try
        If Not File.Exists(outputFile) Then
            MessageBox.Show(translations(currentLanguage)("msg_file_not_found"), translations(currentLanguage)("msg_error_title"), MessageBoxButtons.OK, MessageBoxIcon.Error)
            Return
        End If
        btnSendToRadio.Enabled = False
        btnSendToRadio.Text = translations(currentLanguage)("btn_sending")
        Try
            Dim url As String = $"http://{selectedIP}/upload"
            Dim boundary As String = "----WebKitFormBoundary" & DateTime.Now.Ticks.ToString("x")
            Dim request As HttpWebRequest = CType(WebRequest.Create(url), HttpWebRequest)
            request.Method = "POST"
            request.ContentType = "multipart/form-data; boundary=" & boundary
            request.UserAgent = "Mozilla/5.0"
            Dim fileBytes As Byte() = File.ReadAllBytes(outputFile)
            Dim headerBytes As Byte() = Encoding.UTF8.GetBytes(
                "--" & boundary & vbCrLf &
                $"Content-Disposition: form-data; name=""plfile""; filename=""output_stations.csv""" & vbCrLf &
                "Content-Type: text/plain" & vbCrLf & vbCrLf)
            Dim footerBytes As Byte() = Encoding.UTF8.GetBytes(vbCrLf & "--" & boundary & "--" & vbCrLf)
            request.ContentLength = headerBytes.Length + fileBytes.Length + footerBytes.Length
            Using requestStream As Stream = request.GetRequestStream()
                requestStream.Write(headerBytes, 0, headerBytes.Length)
                requestStream.Write(fileBytes, 0, fileBytes.Length)
                requestStream.Write(footerBytes, 0, footerBytes.Length)
            End Using
            Using response As HttpWebResponse = CType(request.GetResponse(), HttpWebResponse)
                If response.StatusCode = HttpStatusCode.OK Then
                    AppendLog(String.Format(translations(currentLanguage)("log_file_send_success"), selectedIP))
                    MessageBox.Show(String.Format(translations(currentLanguage)("msg_send_file_success"), selectedIP), translations(currentLanguage)("msg_success_title"), MessageBoxButtons.OK, MessageBoxIcon.Information)
                Else
                    AppendLog(String.Format(translations(currentLanguage)("log_file_send_error"), response.StatusCode))
                    MessageBox.Show(translations(currentLanguage)("msg_send_file_error"), translations(currentLanguage)("msg_error_title"), MessageBoxButtons.OK, MessageBoxIcon.Error)
                End If
            End Using
        Catch ex As Exception
            AppendLog(String.Format(translations(currentLanguage)("msg_send_file_error"), ex.Message))
            MessageBox.Show(translations(currentLanguage)("msg_send_file_error"), translations(currentLanguage)("msg_error_title"), MessageBoxButtons.OK, MessageBoxIcon.Error)
        Finally
            btnSendToRadio.Enabled = True
            btnSendToRadio.Text = translations(currentLanguage)("btn_send_to_radio")
        End Try
    End Sub
    Private Function GetLocalIPAddress() As String
        Try
            Dim host As String = Dns.GetHostName()
            Dim ipEntry As IPHostEntry = Dns.GetHostEntry(host)
            For Each ip As IPAddress In ipEntry.AddressList
                If ip.AddressFamily = AddressFamily.InterNetwork AndAlso Not ip.ToString().StartsWith("127.") Then
                    Return ip.ToString()
                End If
            Next
        Catch ex As Exception
            AppendLog(String.Format(translations(currentLanguage)("msg_no_local_ip"), ex.Message))
        End Try
        Return String.Empty
    End Function
    Private Sub AppendLog(text As String)
        Dim line As String = $"[{DateTime.Now:HH:mm:ss}] {text}{Environment.NewLine}"
        Dim doAppend As Action =
            Sub()
                ' Ograniczamy rozmiar logu, żeby przy dziesiątkach tysięcy stacji
                ' pole tekstowe nie puchło i nie zamulało interfejsu.
                If txtLog.TextLength > 120000 Then
                    txtLog.Text = txtLog.Text.Substring(txtLog.TextLength - 60000)
                End If
                txtLog.AppendText(line)
                txtLog.SelectionStart = txtLog.TextLength
                txtLog.ScrollToCaret()
            End Sub
        ' BeginInvoke (asynchronicznie) – wątki robocze nie blokują się na UI.
        If txtLog.InvokeRequired Then
            Try
                txtLog.BeginInvoke(doAppend)
            Catch
            End Try
        Else
            doAppend()
        End If
    End Sub
    Private Function CleanStationName(name As String) As String
        If String.IsNullOrWhiteSpace(name) Then Return name
        Return name.Trim()
    End Function
    Private Function DeriveNameFromUrl(url As String) As String
        Try
            Dim uri As New Uri(url)
            Dim host = uri.Host.Replace("www.", "").Replace(".com", "").Replace(".pl", "").Replace(".org", "")
            Dim segments = uri.Segments.Select(Function(s) s.TrimEnd("/"c)).Where(Function(s) Not String.IsNullOrEmpty(s)).ToList()
            Dim name = If(segments.Any(), segments.Last().Replace(".mp3", "").Replace(".m3u", "").Replace(".pls", "").Replace(";", ""), host)
            name = name.Replace("-", " ").Replace("_", " ").Trim()
            Return CleanStationName(name)
        Catch
            Return CleanStationName(url)
        End Try
    End Function
    Private Async Sub btnSelectFile_Click(sender As Object, e As EventArgs)
        stations.Clear()
        dgvStations.Rows.Clear()
        txtLog.Clear()
        progressBar.Value = 0
        checkedStationsCount = 0
        totalStationsCount = 0
        lblProgress.Text = translations(currentLanguage)("lbl_progress_initial")
        AppendLog(translations(currentLanguage)("log_processing_file"))
        Dim ofd As New OpenFileDialog() With {
            .Filter = translations(currentLanguage)("file_filter"),
            .InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop)
        }
        If ofd.ShowDialog() <> DialogResult.OK Then Return
        Dim filePath As String = ofd.FileName
        Dim lines() As String
        Try
            lines = File.ReadAllLines(filePath)
        Catch ex As Exception
            MessageBox.Show(String.Format(translations(currentLanguage)("msg_file_read_error"), ex.Message), translations(currentLanguage)("msg_error_title"), MessageBoxButtons.OK, MessageBoxIcon.Error)
            Return
        End Try
        AppendLog(translations(currentLanguage)("log_processing_file"))
        For i As Integer = 0 To lines.Length - 1
            Dim line As String = lines(i).Trim()
            If String.IsNullOrEmpty(line) Then Continue For
            Dim parts() As String = line.Split(vbTab)
            Dim url As String
            Dim name As String
            Dim volume As String = "0"
            If parts.Length >= 3 Then
                name = CleanStationName(parts(0).Trim())
                url = parts(1).Trim()
                volume = parts(2).Trim()
            ElseIf parts.Length = 2 Then
                name = CleanStationName(parts(0).Trim())
                url = parts(1).Trim()
            Else
                url = line.Trim()
                If Not Regex.IsMatch(url, "^https?://[^\s]+$") Then
                    AppendLog(String.Format(translations(currentLanguage)("log_invalid_url"), i + 1, url))
                    Continue For
                End If
                name = DeriveNameFromUrl(url)
            End If
            stations.Add(New Station With {.Name = name, .URL = url, .Volume = volume})
        Next
        If stations.Count = 0 Then
            MessageBox.Show(translations(currentLanguage)("msg_no_valid_urls"), translations(currentLanguage)("msg_error_title"), MessageBoxButtons.OK, MessageBoxIcon.Information)
            Return
        End If
        totalStationsCount = stations.Count
        AppendLog(String.Format(translations(currentLanguage)("log_urls_loaded"), totalStationsCount))
        lblProgress.Text = String.Format(translations(currentLanguage)("lbl_progress_initial").Replace("0/0", "0/{0}"), totalStationsCount)
        Dim totalBefore = stations.Count
        Dim working = Await CheckStationsAsync(stations)
        stations = working
        UpdateDataGridView()
        AppendLog(String.Format(translations(currentLanguage)("log_stations_processed"), stations.Count, totalBefore - stations.Count))
    End Sub
    Private Async Sub btnSelectCountry_Click(sender As Object, e As EventArgs)
        Try
            Dim cached = TryLoadCache()
            If cached IsNot Nothing Then
                ' Cache jest świeży — zapytaj użytkownika co chce zrobić.
                Dim fi As New FileInfo(CacheFilePath)
                Dim age = DateTime.UtcNow - fi.LastWriteTimeUtc
                Dim ageStr = If(age.TotalMinutes < 60,
                    $”{CInt(age.TotalMinutes)} min temu”,
                    $”{CInt(age.TotalHours)} h temu”)
                Dim msg = String.Format(translations(currentLanguage)(“msg_cache_ask”),
                                        cached.Count, ageStr)
                Dim dlg = MessageBox.Show(msg,
                                          translations(currentLanguage)(“msg_cache_ask_title”),
                                          MessageBoxButtons.YesNo,
                                          MessageBoxIcon.Question)
                If dlg = DialogResult.Yes Then
                    ' Użyj cache — otwórz natychmiast
                    AppendLog(String.Format(translations(currentLanguage)(“log_cache_loaded”), cached.Count))
                    ShowStationPickerFromCache(cached)
                    Return
                End If
                ' Nie — pobierz świeże (cache zostaje, zostanie nadpisany po nowym sprawdzeniu)
            End If
            AppendLog(translations(currentLanguage)(“log_fetching_all”))
            Dim all = Await SearchAllSourcesAsync(“”)
            Await ProcessAndShowStationsAsync(all, translations(currentLanguage)(“src_all”))
        Catch ex As Exception
            MessageBox.Show(String.Format(translations(currentLanguage)(“msg_country_fetch_error”), ex.Message), translations(currentLanguage)(“msg_error_title”), MessageBoxButtons.OK, MessageBoxIcon.Error)
        End Try
    End Sub

    ' ====== Cache stacji (plik JSON, odświeżany co 24h) ======

    ' Zwraca listę stacji z cache lub Nothing jeśli cache jest nieaktualny/brak.
    Private Function TryLoadCache() As List(Of Station)
        Try
            If Not File.Exists(CacheFilePath) Then Return Nothing
            Dim fi As New FileInfo(CacheFilePath)
            If DateTime.UtcNow - fi.LastWriteTimeUtc > TimeSpan.FromHours(CACHE_MAX_AGE_HOURS) Then
                AppendLog(translations(currentLanguage)(“log_cache_expired”))
                Return Nothing
            End If
            Dim json = File.ReadAllText(CacheFilePath, Encoding.UTF8)
            ' Newtonsoft.Json — poprawnie dekoduje wszystkie znaki specjalne w nazwach
            Dim entries = Newtonsoft.Json.JsonConvert.DeserializeObject(Of List(Of CacheEntry))(json)
            If entries Is Nothing OrElse entries.Count = 0 Then Return Nothing
            Return entries.Select(Function(e) New Station With {
                .Name = If(e.n, “”),
                .URL = If(e.u, “”),
                .Volume = If(e.v, “0”)
            }).ToList()
        Catch ex As Exception
            AppendLog(“Cache odczyt blad: “ & ex.Message)
            Return Nothing
        End Try
    End Function

    ' Zapisuje listę sprawdzonych stacji do pliku cache.
    ' Pomocnicza klasa do serializacji cache stacji.
    Private Class CacheEntry
        Public Property n As String   ' name
        Public Property u As String   ' url
        Public Property v As String   ' volume
    End Class

    Private Sub SaveCache(workingStations As List(Of Station))
        Try
            Dim entries = workingStations.Select(Function(s) New CacheEntry With {
                .n = s.Name, .u = s.URL, .v = s.Volume}).ToList()
            ' Newtonsoft.Json obsługuje poprawnie escaping cudzysłowów, backslashy itp.
            Dim json = Newtonsoft.Json.JsonConvert.SerializeObject(entries)
            File.WriteAllText(CacheFilePath, json, Encoding.UTF8)
            AppendLog(String.Format(translations(currentLanguage)(“log_cache_saved”), workingStations.Count, CacheFilePath))
        Catch ex As Exception
            AppendLog(“Cache nie zapisany: “ & ex.Message)
        End Try
    End Sub

    ' Pokazuje okno wyboru z gotowymi stacjami z cache (bez sprawdzania).
    Private Sub ShowStationPickerFromCache(cached As List(Of Station))
        Dim dummy As DataGridView = Nothing
        Dim dummyLbl As Label = Nothing
        Dim dummyStatus As Label = Nothing
        Dim allSt As New List(Of Station)(cached)
        _pickerFilter = “”
        Dim f = BuildPickerForm(allSt, dummy, dummyLbl, dummyStatus)
        ' Wypełnij tabelę od razu (cache nie wymaga sprawdzania).
        Dim dgv = dummy
        dgv.SuspendLayout()
        If allSt.Count > 0 Then
            Dim rows(allSt.Count - 1) As DataGridViewRow
            For i = 0 To allSt.Count - 1
                Dim r As New DataGridViewRow()
                r.CreateCells(dgv, False, Nothing, allSt(i).Name, allSt(i).URL)
                rows(i) = r
            Next
            dgv.Rows.AddRange(rows)
        End If
        dgv.ResumeLayout()
        dummyLbl.Text = String.Format(translations(currentLanguage)(“lbl_found_working”), dgv.Rows.Count)
        AddHandler f.FormClosed, Sub() StopPlayerWmp()
        f.ShowDialog()
    End Sub

    ' ====== Pobieranie stacji z różnych źródeł ======

    ' Odpytuje wszystkie źródła równolegle i łączy wyniki w jedną listę.
    Private Async Function SearchAllSourcesAsync(query As String) As Task(Of List(Of Station))
        Dim tasks As New List(Of Task(Of List(Of Station))) From {
            SafeFetchAsync("Radio Browser", Function() RadioBrowserSearchAsync(query)),
            SafeFetchAsync("SomaFM", Function() SomaFmSearchAsync(query)),
            SafeFetchAsync("yoRadio", Function() YoRadioSearchAsync(query)),
            SafeFetchAsync("Internet-Radio", Function() InternetRadioSearchAsync(query)),
            SafeFetchAsync("rcast.net", Function() RcastSearchAsync(query)),
            SafeFetchAsync("SHOUTcast", Function() ShoutcastSearchAsync(query)),
            SafeFetchAsync("OnlineRadioBox", Function() OnlineRadioBoxSearchAsync(query))
        }
        Dim results = Await Task.WhenAll(tasks)
        ' Bez limitu – bierzemy wszystko, co zwróciły źródła.
        Return results.SelectMany(Function(r) r).ToList()
    End Function

    ' Bezpieczne pobranie z jednego źródła – błąd nie przerywa pozostałych.
    Private Async Function SafeFetchAsync(name As String, fetch As Func(Of Task(Of List(Of Station)))) As Task(Of List(Of Station))
        Try
            Dim r = Await fetch()
            AppendLog(String.Format(translations(currentLanguage)("log_source_count"), name, r.Count))
            Return r
        Catch ex As Exception
            AppendLog(String.Format(translations(currentLanguage)("log_source_skipped"), name, ex.Message))
            Return New List(Of Station)
        End Try
    End Function

    ' Prosty klient HTTP do zapytań API (z User-Agent i limitem czasu).
    Private Async Function ApiGetStringAsync(url As String, Optional timeoutSec As Integer = 15, Optional userAgent As String = Nothing) As Task(Of String)
        Using handler As New HttpClientHandler With {
            .AllowAutoRedirect = True,
            .AutomaticDecompression = DecompressionMethods.GZip Or DecompressionMethods.Deflate
        }
            Using client As New HttpClient(handler)
                client.Timeout = TimeSpan.FromSeconds(timeoutSec)
                client.DefaultRequestHeaders.UserAgent.ParseAdd(If(userAgent, "sprawdzanie-list/1.0 (+https://github.com/seba99317)"))
                ' Nagłówki jak w przeglądarce – niektóre serwisy (np. OnlineRadioBox za
                ' Cloudflare) bez Accept-Language zwracają stronę-wyzwanie zamiast treści.
                client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,application/json,*/*;q=0.8")
                client.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9,pl;q=0.8")
                Dim resp = Await client.GetAsync(url)
                resp.EnsureSuccessStatusCode()
                Return Await resp.Content.ReadAsStringAsync()
            End Using
        End Using
    End Function

    ' Pobranie strony HTML – używa nagłówka przeglądarki, bo wiele katalogów
    ' radiowych odrzuca nietypowe User-Agenty.
    Private Const BROWSER_UA As String = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0 Safari/537.36"
    Private Async Function HtmlGetAsync(url As String, Optional timeoutSec As Integer = 20) As Task(Of String)
        Return Await ApiGetStringAsync(url, timeoutSec, BROWSER_UA)
    End Function

    ' Zapytanie POST (form-urlencoded) – używane przez API katalogu SHOUTcast.
    Private Async Function ApiPostStringAsync(url As String, formData As String, Optional timeoutSec As Integer = 25) As Task(Of String)
        Using handler As New HttpClientHandler With {
            .AllowAutoRedirect = True,
            .AutomaticDecompression = DecompressionMethods.GZip Or DecompressionMethods.Deflate
        }
            Using client As New HttpClient(handler)
                client.Timeout = TimeSpan.FromSeconds(timeoutSec)
                client.DefaultRequestHeaders.UserAgent.ParseAdd(BROWSER_UA)
                client.DefaultRequestHeaders.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest")
                client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/json, text/javascript, */*; q=0.01")
                Dim content As New StringContent(formData, Encoding.UTF8, "application/x-www-form-urlencoded")
                Dim resp = Await client.PostAsync(url, content)
                resp.EnsureSuccessStatusCode()
                Return Await resp.Content.ReadAsStringAsync()
            End Using
        End Using
    End Function

    ' Radio Browser udostępnia sieć serwerów lustrzanych – pobieramy ich listę
    ' i mieszamy, aby rozłożyć obciążenie i mieć automatyczny failover.
    Private Async Function GetRadioBrowserServersAsync() As Task(Of List(Of String))
        Dim servers As New List(Of String)
        Try
            Dim json = Await ApiGetStringAsync("https://all.api.radio-browser.info/json/servers")
            Dim doc = JsonDocument.Parse(json)
            For Each s In doc.RootElement.EnumerateArray()
                Dim nameEl As JsonElement
                If s.TryGetProperty("name", nameEl) Then
                    Dim n = nameEl.GetString()
                    If Not String.IsNullOrEmpty(n) Then servers.Add($"https://{n}")
                End If
            Next
        Catch
        End Try
        servers = servers.Distinct().ToList()
        If servers.Count = 0 Then
            servers.AddRange({"https://de1.api.radio-browser.info", "https://de2.api.radio-browser.info",
                              "https://nl1.api.radio-browser.info", "https://at1.api.radio-browser.info"})
        End If
        Dim rnd As New Random()
        Return servers.OrderBy(Function(x) rnd.Next()).ToList()
    End Function

    ' --- Radio Browser: wyszukiwanie po nazwie lub najpopularniejsze (globalnie) ---
    Private Async Function RadioBrowserSearchAsync(query As String) As Task(Of List(Of Station))
        Dim servers = Await GetRadioBrowserServersAsync()
        ' Serwer ma limit 1000 per request – paginujemy przez offset.
        Dim all As New List(Of Station)
        Dim baseUrl As String = Nothing
        For Each srv In servers
            Try
                Dim test = Await ApiGetStringAsync(srv & "/json/stations?limit=1&offset=0&hidebroken=true", 15)
                baseUrl = srv
                Exit For
            Catch
            End Try
        Next
        If baseUrl Is Nothing Then Return all
        Dim offset As Integer = 0
        Dim pageSize As Integer = 1000
        Do
            Dim path As String = If(String.IsNullOrEmpty(query),
                $"/json/stations?order=votes&reverse=true&hidebroken=true&limit={pageSize}&offset={offset}",
                $"/json/stations/byname/{Uri.EscapeDataString(query)}?hidebroken=true&order=votes&reverse=true&limit={pageSize}&offset={offset}")
            Try
                Dim json = Await ApiGetStringAsync(baseUrl & path, 30)
                Dim page = ParseRadioBrowserStations(json)
                If page.Count = 0 Then Exit Do
                all.AddRange(page)
                AppendLog($"   Radio Browser: {all.Count} stacji...")
                offset += pageSize
                If page.Count < pageSize Then Exit Do  ' ostatnia strona
            Catch
                Exit Do
            End Try
        Loop
        Return all
    End Function

    Private Function ParseRadioBrowserStations(json As String) As List(Of Station)
        Dim list As New List(Of Station)
        If String.IsNullOrEmpty(json) Then Return list
        For Each s In JsonDocument.Parse(json).RootElement.EnumerateArray()
            Dim nameEl As JsonElement, urlEl As JsonElement, resEl As JsonElement
            Dim title = If(s.TryGetProperty("name", nameEl), nameEl.GetString(), "")
            Dim u As String = ""
            If s.TryGetProperty("url_resolved", resEl) AndAlso resEl.ValueKind = JsonValueKind.String AndAlso Not String.IsNullOrEmpty(resEl.GetString()) Then
                u = resEl.GetString()
            ElseIf s.TryGetProperty("url", urlEl) Then
                u = urlEl.GetString()
            End If
            If Not String.IsNullOrWhiteSpace(u) Then
                list.Add(New Station With {.Name = CleanStationName(title), .URL = u, .Volume = "0"})
            End If
        Next
        Return list
    End Function

    ' --- yoRadio: pobiera całą bazę (wszystkie kraje) raz i filtruje lokalnie ---
    Private Async Function YoRadioSearchAsync(query As String) As Task(Of List(Of Station))
        If yoRadioCache Is Nothing Then
            yoRadioCache = Await CrawlYoRadioAsync()
        End If
        If String.IsNullOrEmpty(query) Then Return yoRadioCache.ToList()
        Return yoRadioCache.Where(Function(s) s.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0).ToList()
    End Function

    Private Async Function CrawlYoRadioAsync() As Task(Of List(Of Station))
        Dim json = Await ApiGetStringAsync("https://yoradio.licar.biz/api/countries")
        Dim ids As New List(Of Integer)
        For Each c In JsonDocument.Parse(json).RootElement.EnumerateArray()
            Dim idEl As JsonElement
            If c.TryGetProperty("id", idEl) AndAlso idEl.ValueKind = JsonValueKind.Number Then ids.Add(idEl.GetInt32())
        Next
        Dim sem As New SemaphoreSlim(12)
        Dim tasks = ids.Select(Function(id) Task.Run(Async Function() As Task(Of List(Of Station))
                                                         Await sem.WaitAsync()
                                                         Try
                                                             Dim sj = Await ApiGetStringAsync($"https://yoradio.licar.biz/api/stations/{id}", 15)
                                                             Dim r As New List(Of Station)
                                                             For Each s In JsonDocument.Parse(sj).RootElement.EnumerateArray()
                                                                 Dim tEl As JsonElement, uEl As JsonElement
                                                                 Dim t = If(s.TryGetProperty("title", tEl), tEl.GetString(), "")
                                                                 Dim u = If(s.TryGetProperty("final_url", uEl), uEl.GetString(), "")
                                                                 If Not String.IsNullOrWhiteSpace(u) Then r.Add(New Station With {.Name = CleanStationName(t), .URL = u, .Volume = "0"})
                                                             Next
                                                             Return r
                                                         Catch
                                                             Return New List(Of Station)
                                                         Finally
                                                             sem.Release()
                                                         End Try
                                                     End Function)).ToList()
        Dim arrays = Await Task.WhenAll(tasks)
        Return arrays.SelectMany(Function(a) a).ToList()
    End Function

    ' --- SomaFM: stała lista kanałów, filtr po nazwie ---
    Private Async Function SomaFmSearchAsync(query As String) As Task(Of List(Of Station))
        Dim json = Await ApiGetStringAsync("https://somafm.com/channels.json")
        Dim list As New List(Of Station)
        For Each ch In JsonDocument.Parse(json).RootElement.GetProperty("channels").EnumerateArray()
            Dim title = ch.GetProperty("title").GetString()
            If query <> "" AndAlso title.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0 Then Continue For
            Dim best As String = Nothing
            For Each pl In ch.GetProperty("playlists").EnumerateArray()
                Dim u = pl.GetProperty("url").GetString()
                If best Is Nothing Then best = u
                Dim fmtEl As JsonElement
                If pl.TryGetProperty("format", fmtEl) AndAlso fmtEl.GetString() = "mp3" Then
                    best = u
                    Exit For
                End If
            Next
            If best IsNot Nothing Then
                Dim direct = Await ResolvePlaylistAsync(best)
                list.Add(New Station With {.Name = CleanStationName(title), .URL = If(direct, best), .Volume = "0"})
            End If
        Next
        Return list
    End Function

    ' Rozwija plik playlisty (.pls/.m3u) do bezpośredniego adresu strumienia.
    Private Async Function ResolvePlaylistAsync(url As String) As Task(Of String)
        Dim info = Await ResolvePlaylistInfoAsync(url)
        Return info.StreamUrl
    End Function

    ' Jak wyżej, ale zwraca też tytuł stacji z pliku PLS/M3U (jeśli dostępny).
    Private Async Function ResolvePlaylistInfoAsync(url As String) As Task(Of (StreamUrl As String, Title As String))
        Try
            ' Jeśli to nie playlista, tylko bezpośredni strumień – zwróć od razu.
            Dim lower = url.ToLowerInvariant()
            If Not (lower.Contains(".pls") OrElse lower.Contains(".m3u")) Then
                Return (url, Nothing)
            End If
            Dim content = Await ApiGetStringAsync(url, 10, BROWSER_UA)
            Dim streamUrl As String = Nothing
            Dim title As String = Nothing
            Dim pendingExtInf As String = Nothing
            For Each ln In content.Split({vbCrLf, vbLf, vbCr}, StringSplitOptions.RemoveEmptyEntries)
                Dim t = ln.Trim()
                If t.StartsWith("File", StringComparison.OrdinalIgnoreCase) AndAlso t.Contains("=") Then
                    If streamUrl Is Nothing Then streamUrl = t.Substring(t.IndexOf("=") + 1).Trim()
                ElseIf t.StartsWith("Title", StringComparison.OrdinalIgnoreCase) AndAlso t.Contains("=") Then
                    If title Is Nothing Then title = t.Substring(t.IndexOf("=") + 1).Trim()
                ElseIf t.StartsWith("#EXTINF", StringComparison.OrdinalIgnoreCase) AndAlso t.Contains(",") Then
                    pendingExtInf = t.Substring(t.IndexOf(",") + 1).Trim()
                ElseIf t.StartsWith("http", StringComparison.OrdinalIgnoreCase) Then
                    If streamUrl Is Nothing Then
                        streamUrl = t
                        If title Is Nothing Then title = pendingExtInf
                    End If
                End If
            Next
            Return (streamUrl, title)
        Catch
        End Try
        Return (Nothing, Nothing)
    End Function

    ' ====== Generyczny scraping katalogów radiowych (linki PLS/M3U) ======

    ' Wyciąga z HTML wszystkie adresy do plików .pls/.m3u/.m3u8 (absolutne i względne).
    Private Function ExtractPlaylistUrls(html As String, pageUrl As String) As List(Of String)
        Dim found As New List(Of String)
        If String.IsNullOrEmpty(html) Then Return found
        ' Dekodujemy encje (np. &amp; → &), bo niektóre serwisy (internet-radio.com)
        ' trzymają prawdziwy adres strumienia w parametrze ?u=http://...listen.pls&t=...
        html = html.Replace("&amp;", "&")
        Dim baseUri As Uri = Nothing
        Uri.TryCreate(pageUrl, UriKind.Absolute, baseUri)
        ' 1) Pełne adresy z rozszerzeniem .pls/.m3u/.m3u8
        For Each m As Match In Regex.Matches(html, "https?://[^\s""'<>()]+?\.(?:pls|m3u8|m3u)(?:\?[^\s""'<>()]*)?", RegexOptions.IgnoreCase)
            found.Add(m.Value)
        Next
        ' 2) rcast.net: stream.rcast.net/pls/NNNNN i stream.rcast.net/m3u/NNNNN
        '    (katalog /pls/ zamiast rozszerzenia .pls – osobny wzorzec)
        For Each m As Match In Regex.Matches(html, "https?://[^\s""'<>()]*?/(?:pls|m3u)/\d+(?:[^\s""'<>()]*)?", RegexOptions.IgnoreCase)
            found.Add(m.Value)
        Next
        ' 3) Względne w atrybutach href/src/data-*
        For Each m As Match In Regex.Matches(html, "(?:href|src|data-href|data-url)\s*=\s*[""']([^""']+?\.(?:pls|m3u8|m3u)(?:\?[^""']*)?)[""']", RegexOptions.IgnoreCase)
            Dim rel = m.Groups(1).Value
            If rel.StartsWith("http", StringComparison.OrdinalIgnoreCase) Then
                found.Add(rel)
            ElseIf baseUri IsNot Nothing Then
                Dim abs As Uri = Nothing
                If Uri.TryCreate(baseUri, rel, abs) Then found.Add(abs.ToString())
            End If
        Next
        Return found.Distinct().ToList()
    End Function

    ' Pobiera podane strony, wyciąga linki do playlist, rozwija je równolegle
    ' do bezpośrednich strumieni i (opcjonalnie) filtruje po nazwie.
    ' Gdy rozwinięcie pliku PLS/M3U się nie uda (np. sieć blokuje serwer strumienia),
    ' zostawiamy sam link do playlisty jako fallback – stacja i tak się pojawi.
    Private Async Function ScrapePlaylistSourceAsync(pageUrls As IEnumerable(Of String), sourceName As String, query As String, Optional cap As Integer = Integer.MaxValue) As Task(Of List(Of Station))
        Dim links As New List(Of String)
        For Each p In pageUrls
            Try
                Dim html = Await HtmlGetAsync(p)
                links.AddRange(ExtractPlaylistUrls(html, p))
            Catch ex As Exception
                AppendLog($"      ⚠️ {sourceName}: błąd pobrania strony – {ex.Message}")
            End Try
        Next
        links = links.Distinct().Take(cap).ToList()
        AppendLog($"      🔗 {sourceName}: znaleziono {links.Count} linków PLS/M3U")
        If links.Count = 0 Then Return New List(Of Station)
        Dim resolvedCount As Integer = 0
        Dim fallbackCount As Integer = 0
        Dim sem As New SemaphoreSlim(12)
        Dim tasks = links.Select(Function(link) Task.Run(Async Function() As Task(Of Station)
                                                             Await sem.WaitAsync()
                                                             Try
                                                                 Dim info = Await ResolvePlaylistInfoAsync(link)
                                                                 If Not String.IsNullOrWhiteSpace(info.StreamUrl) Then
                                                                     Interlocked.Increment(resolvedCount)
                                                                     Dim nm = If(Not String.IsNullOrWhiteSpace(info.Title), CleanStationName(info.Title), DeriveNameFromUrl(info.StreamUrl))
                                                                     Return New Station With {.Name = nm, .URL = info.StreamUrl, .Volume = "0"}
                                                                 End If
                                                             Catch
                                                             Finally
                                                                 sem.Release()
                                                             End Try
                                                             ' fallback – sam link do playlisty
                                                             Interlocked.Increment(fallbackCount)
                                                             Return New Station With {.Name = DeriveNameFromUrl(link), .URL = link, .Volume = "0"}
                                                         End Function)).ToList()
        Dim resolved = (Await Task.WhenAll(tasks)).Where(Function(s) s IsNot Nothing).ToList()
        AppendLog($"      ✓ {sourceName}: rozwinięto {resolvedCount}, fallback {fallbackCount}")
        If query <> "" Then
            resolved = resolved.Where(Function(s) s.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 OrElse s.URL.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0).ToList()
        End If
        Return resolved
    End Function

    ' --- Internet-Radio.com: ma wyszukiwarkę zwracającą listę z linkami .pls ---
    Private Async Function InternetRadioSearchAsync(query As String) As Task(Of List(Of Station))
        Dim pages As New List(Of String)
        If query <> "" Then
            pages.Add($"https://www.internet-radio.com/search/?radio={Uri.EscapeDataString(query)}")
        Else
            ' Strony popularnych gatunków – mają bezpośrednie linki .pls/.m3u przy stacjach.
            pages.Add("https://www.internet-radio.com/stations/pop/")
            pages.Add("https://www.internet-radio.com/stations/rock/")
            pages.Add("https://www.internet-radio.com/stations/dance/")
        End If
        Return Await ScrapePlaylistSourceAsync(pages, "Internet-Radio", "")
    End Function

    ' --- rcast.net: katalog stacji z przyciskami PLS/M3U ---
    Private Async Function RcastSearchAsync(query As String) As Task(Of List(Of Station))
        ' Katalog rcast nie ma wyszukiwania po nazwie, więc pobieramy całą listę,
        ' a ewentualne filtrowanie po nazwie robimy lokalnie (w ScrapePlaylistSourceAsync).
        Return Await ScrapePlaylistSourceAsync({"https://www.rcast.net/dir"}, "rcast.net", query)
    End Function

    ' --- SHOUTcast: oficjalne API katalogu (JSON) + tunein PLS dla każdego ID ---
    Private Async Function ShoutcastSearchAsync(query As String) As Task(Of List(Of Station))
        Dim q = If(String.IsNullOrEmpty(query), "radio", query)
        ' Timeout 40s – SHOUTcast bywa wolny; przy 25s czasem się nie wyrabia.
        Dim json = Await ApiPostStringAsync("https://directory.shoutcast.com/Search/UpdateSearch", "query=" & Uri.EscapeDataString(q), 40)
        Dim entries As New List(Of (Id As Long, Name As String))
        For Each e In JsonDocument.Parse(json).RootElement.EnumerateArray()
            Dim idEl As JsonElement, nmEl As JsonElement
            If e.TryGetProperty("ID", idEl) AndAlso idEl.ValueKind = JsonValueKind.Number Then
                Dim nm = If(e.TryGetProperty("Name", nmEl), nmEl.GetString(), "")
                entries.Add((idEl.GetInt64(), nm))
            End If
        Next
        ' Nie rozwijamy plików PLS z góry (przy 5000 stacjach to trwałoby kilkadziesiąt
        ' minut i wyglądałoby jak zawieszenie). Zwracamy linki tunein (.pls) od razu –
        ' ich sprawdzenie robi już faza „Sprawdzam” z paskiem postępu.
        Dim result As New List(Of Station)
        For Each en In entries
            Dim tuneIn = $"https://yp.shoutcast.com/sbin/tunein-station.pls?id={en.Id}"
            Dim nmBase = If(Not String.IsNullOrWhiteSpace(en.Name), CleanStationName(en.Name), DeriveNameFromUrl(tuneIn))
            result.Add(New Station With {.Name = nmBase, .URL = tuneIn, .Volume = "0"})
        Next
        AppendLog($"      🔗 SHOUTcast: {result.Count} stacji z katalogu")
        Return result
    End Function

    ' --- OnlineRadioBox: scrape 2-poziomowy (lista stacji → strona stacji → strumień) ---
    Private Async Function OnlineRadioBoxSearchAsync(query As String) As Task(Of List(Of Station))
        Dim startUrl = If(String.IsNullOrEmpty(query),
                          "https://onlineradiobox.com/",
                          $"https://onlineradiobox.com/search?q={Uri.EscapeDataString(query)}")
        Dim html = Await HtmlGetAsync(startUrl)
        Dim links As New List(Of String)
        For Each m As Match In Regex.Matches(html, "href=""(/[a-z]{2}/[a-z0-9._\-]+/)""", RegexOptions.IgnoreCase)
            links.Add("https://onlineradiobox.com" & m.Groups(1).Value)
        Next
        links = links.Distinct().ToList()
        If links.Count = 0 Then Return New List(Of Station)
        Dim sem As New SemaphoreSlim(8)
        Dim tasks = links.Select(Function(link) Task.Run(Async Function() As Task(Of Station)
                                                             Await sem.WaitAsync()
                                                             Try
                                                                 Dim page = Await HtmlGetAsync(link)
                                                                 Dim stream = ExtractStreamUrls(page).FirstOrDefault()
                                                                 If String.IsNullOrWhiteSpace(stream) Then Return Nothing
                                                                 Return New Station With {.Name = ExtractPageTitle(page, link), .URL = stream, .Volume = "0"}
                                                             Catch
                                                                 Return Nothing
                                                             Finally
                                                                 sem.Release()
                                                             End Try
                                                         End Function)).ToList()
        Dim resolved = (Await Task.WhenAll(tasks)).Where(Function(s) s IsNot Nothing).ToList()
        If query <> "" Then
            resolved = resolved.Where(Function(s) s.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 OrElse s.URL.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0).ToList()
        End If
        Return resolved
    End Function

    ' Wyciąga z HTML bezpośrednie adresy strumieni (audio) – dla stron, które nie
    ' używają plików PLS/M3U, tylko podają link do strumienia wprost.
    Private Function ExtractStreamUrls(html As String) As List(Of String)
        Dim res As New List(Of String)
        If String.IsNullOrEmpty(html) Then Return res
        html = html.Replace("&amp;", "&")
        ' Priorytet: atrybuty/pola wskazujące strumień wprost (OnlineRadioBox:
        ' stream="https://...", też data-stream= oraz JSON "stream":"...").
        For Each m As Match In Regex.Matches(html, "(?:""stream""|stream|data-stream)\s*[:=]\s*""(https?://[^""]+)""", RegexOptions.IgnoreCase)
            res.Add(m.Groups(1).Value)
        Next
        ' Następnie adresy z typowym rozszerzeniem audio / playlisty.
        For Each m As Match In Regex.Matches(html, "https?://[^\s""'<>]+?\.(?:m3u8|m3u|pls|aac|mp3|ogg)(?:\?[^\s""'<>]*)?", RegexOptions.IgnoreCase)
            res.Add(m.Value)
        Next
        For Each m As Match In Regex.Matches(html, "https?://playerservices\.streamtheworld\.com/[^\s""'<>]+", RegexOptions.IgnoreCase)
            res.Add(m.Value)
        Next
        Return res.Distinct().ToList()
    End Function

    ' Nazwa stacji z nagłówka <h1> strony, w razie braku – z adresu URL.
    Private Function ExtractPageTitle(html As String, url As String) As String
        Dim m = Regex.Match(html, "<h1[^>]*>(.*?)</h1>", RegexOptions.IgnoreCase Or RegexOptions.Singleline)
        If m.Success Then
            Dim t = Regex.Replace(m.Groups(1).Value, "<[^>]+>", "").Trim()
            t = System.Net.WebUtility.HtmlDecode(t)
            If Not String.IsNullOrWhiteSpace(t) Then Return CleanStationName(t)
        End If
        Return DeriveNameFromUrl(url)
    End Function

    ' Formaty obsługiwane przez yoRadio (audio). Odrzucamy wideo (mp4, ts, mkv…)
    ' i wszelkie inne nieodtwarzalne strumienie – po co sprawdzać coś, czego radio nie zagra.
    Private Function IsAudioUrl(url As String) As Boolean
        If String.IsNullOrWhiteSpace(url) Then Return False
        ' Linki bez rozszerzenia (np. /stream, /live, tunein .pls) – przepuszczamy,
        ' bo mogą być wszystkim; CheckStationAsync zweryfikuje je przez HTTP.
        Dim lower = url.ToLowerInvariant()
        ' Jawne rozszerzenia wideo – odrzucamy
        If Regex.IsMatch(lower, "\.(mp4|m4v|mkv|ts|mpeg|avi|mov|flv|webm|f4v)(\?|$)") Then Return False
        ' HLS wideo – jeśli nie jest to m3u8 audio (radia często używają .m3u8)
        ' – pozostawiamy, bo audio HLS jest OK dla yoRadio
        Return True
    End Function

    ' Normalizuje nazwę stacji do klucza dedup (lowercase, tylko litery/cyfry/spacje).
    Private Function NormalizeName(name As String) As String
        If String.IsNullOrWhiteSpace(name) Then Return ""
        Return Regex.Replace(name.ToLowerInvariant().Trim(), "[^a-z0-9 ]", "").Trim()
    End Function

    ' Wspólny etap: usuń duplikaty (URL + nazwa), filtruj formaty, sprawdź działające.
    Private Async Function ProcessAndShowStationsAsync(newStations As List(Of Station), sourceName As String) As Task
        Dim beforeDedup = newStations.Count

        ' Krok 1: filtr formatu + dedup po URL
        newStations = newStations.
            Where(Function(s) Not String.IsNullOrWhiteSpace(s.URL) AndAlso Not String.IsNullOrWhiteSpace(s.Name)).
            Where(Function(s) IsAudioUrl(s.URL)).
            GroupBy(Function(s) s.URL.TrimEnd("/"c).ToLowerInvariant()).
            Select(Function(g) g.First()).
            ToList()

        ' Krok 2: dedup po nazwie — Radio Browser i yoRadio mają te same stacje
        ' pod różnymi URL-ami; zostawiamy pierwszą (zwykle z Radio Browser = lepsza jakość).
        newStations = newStations.
            GroupBy(Function(s) NormalizeName(s.Name)).
            Where(Function(g) g.Key <> "").
            Select(Function(g) g.First()).
            ToList()

        Dim afterDedup = newStations.Count
        AppendLog(String.Format(translations(currentLanguage)("log_dedup"), beforeDedup, afterDedup, beforeDedup - afterDedup))
        If newStations.Count = 0 Then
            MessageBox.Show(String.Format(translations(currentLanguage)("msg_no_stations_for_country"), sourceName), translations(currentLanguage)("msg_error_title"), MessageBoxButtons.OK, MessageBoxIcon.Information)
            Return
        End If
        AppendLog(String.Format(translations(currentLanguage)("log_stations_fetched"), newStations.Count, sourceName))

        ' Otwieramy okno od razu – stacje pojawiają się na żywo w miarę sprawdzania,
        ' nie trzeba czekać na zakończenie całości.
        Dim pickerDgv As DataGridView = Nothing
        Dim pickerLblCount As Label = Nothing
        Dim pickerLblStatus As Label = Nothing
        Dim pickerAllStations As New List(Of Station)   ' pełna lista działających (dla filtra)
        _pickerFilter = ""

        ' Callback "Sprawdź ponownie" — czyści cache dla tych URL-i i sprawdza od nowa.
        Dim recheckCb As Func(Of Task) =
            Async Function() As Task
                ' Usuń wpisy z pamięci podręcznej dla wszystkich stacji z tej sesji.
                For Each st In newStations
                    Dim dummy As (Boolean, String)
                    stationCache.TryRemove(st.URL, dummy)
                Next
                SyncLock pickerAllStations
                    pickerAllStations.Clear()
                End SyncLock
                Try
                    pickerDgv.BeginInvoke(Sub()
                                              pickerDgv.Rows.Clear()
                                              pickerLblCount.Text = translations(currentLanguage)("lbl_checking")
                                          End Sub)
                Catch
                End Try
                Dim onW As Action(Of Station) = Nothing ' forward decl
                onW = Sub(st2)  ' ponownie użyj tego samego onWorking
                          SyncLock pickerAllStations
                              pickerAllStations.Add(st2)
                          End SyncLock
                          If _pickerFilter = "" OrElse st2.Name.IndexOf(_pickerFilter, StringComparison.OrdinalIgnoreCase) >= 0 OrElse st2.URL.IndexOf(_pickerFilter, StringComparison.OrdinalIgnoreCase) >= 0 Then
                              Try
                                  pickerDgv.BeginInvoke(Sub()
                                                            Dim r As New DataGridViewRow()
                                                            r.CreateCells(pickerDgv, False, Nothing, st2.Name, st2.URL)
                                                            pickerDgv.Rows.Add(r)
                                                            pickerLblCount.Text = String.Format(translations(currentLanguage)("lbl_found_working"), pickerDgv.Rows.Count)
                                                        End Sub)
                              Catch
                              End Try
                          End If
                      End Sub
                Await CheckStationsAsync(newStations, False, onW)
                Dim snap As List(Of Station)
                SyncLock pickerAllStations : snap = pickerAllStations.ToList() : End SyncLock
                Task.Run(Sub() SaveCache(snap))
                Task.Run(Sub() SaveUrlCheckCache())
                Try
                    pickerDgv.BeginInvoke(Sub()
                                              pickerLblCount.Text = String.Format(translations(currentLanguage)("lbl_check_done"), pickerDgv.Rows.Count)
                                          End Sub)
                Catch
                End Try
            End Function

        Dim selStationsForm = BuildPickerForm(pickerAllStations, pickerDgv, pickerLblCount, pickerLblStatus, recheckCb)
        ' Zatrzymaj VLC gdy użytkownik zamknie okno dodawania.
        AddHandler selStationsForm.FormClosed, Sub() StopPlayerWmp()
        selStationsForm.Show()   ' natychmiast – nie blokuje

        ' Callback wywoływany na żywo dla każdej działającej stacji.
        Dim onWorking As Action(Of Station) =
            Sub(st)
                SyncLock pickerAllStations
                    pickerAllStations.Add(st)
                End SyncLock
                ' Dodaj wiersz do tabeli tylko jeśli pasuje do aktualnego filtra.
                If _pickerFilter = "" OrElse st.Name.IndexOf(_pickerFilter, StringComparison.OrdinalIgnoreCase) >= 0 OrElse st.URL.IndexOf(_pickerFilter, StringComparison.OrdinalIgnoreCase) >= 0 Then
                    Try
                        pickerDgv.BeginInvoke(Sub()
                                                  Dim r As New DataGridViewRow()
                                                  r.CreateCells(pickerDgv, False, Nothing, st.Name, st.URL)
                                                  pickerDgv.Rows.Add(r)
                                                  pickerLblCount.Text = String.Format(translations(currentLanguage)("lbl_found_working"), pickerDgv.Rows.Count)
                                              End Sub)
                    Catch
                    End Try
                End If
            End Sub

        Await CheckStationsAsync(newStations, False, onWorking)

        ' Sprawdzanie zakończone – zapisz cache i aktualizuj status w oknie.
        Dim snapshot As List(Of Station)
        SyncLock pickerAllStations
            snapshot = pickerAllStations.ToList()
        End SyncLock
        Task.Run(Sub() SaveCache(snapshot))
        Task.Run(Sub() SaveUrlCheckCache())
        Try
            If Not selStationsForm.IsDisposed Then
                selStationsForm.BeginInvoke(Sub()
                                                If pickerLblCount IsNot Nothing Then
                                                    pickerLblCount.Text = String.Format(translations(currentLanguage)("lbl_check_done"), snapshot.Count)
                                                End If
                                            End Sub)
            End If
        Catch
        End Try
    End Function

    ' Okno wyboru stacji do dodania (z zaznaczaniem checkboxem).
    ' Buduje okno wyboru stacji – zwraca formularz i referencje do kontrolek,
    ' by można było dodawać wiersze na żywo z zewnątrz (podczas sprawdzania).
    ' pickerAllStations – lista wszystkich dotychczas znalezionych stacji (do przefiltrowania po zmianie filtra).
    ' pickerFilter – bieżący tekst filtra (ref przez closure).
    ' Bieżący filtr w oknie wyboru – pole klasy, bo VB.NET nie pozwala
    ' używać ByRef wewnątrz lambd (błąd BC36639).
    Private _pickerFilter As String = ""

    Private Function BuildPickerForm(pickerAllStations As List(Of Station),
                                     ByRef outDgv As DataGridView,
                                     ByRef outLblCount As Label,
                                     ByRef outLblStatus As Label,
                                     Optional recheckCallback As Func(Of Task) = Nothing) As Form
        Dim selStationsForm As New Form With {
            .Text = translations(currentLanguage)("select_stations_title"),
            .Size = New Size(820, 640),
            .FormBorderStyle = FormBorderStyle.Sizable,
            .MaximizeBox = True,
            .MinimizeBox = True,
            .StartPosition = FormStartPosition.CenterScreen,
            .BackColor = ThemeBg
        }
        Dim dgv As New DataGridView With {
            .RowHeadersVisible = False,
            .Dock = DockStyle.Fill,
            .Font = New Font("Segoe UI", 10, FontStyle.Regular),
            .AllowUserToAddRows = False,
            .AllowUserToDeleteRows = False,
            .ReadOnly = False,
            .SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            .MultiSelect = False,
            .AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill
        }
        StyleGrid(dgv)
        ' Col 0: ✔ checkbox zaznaczania
        dgv.Columns.Add(New DataGridViewCheckBoxColumn With {.HeaderText = "✔", .Width = 38, .FillWeight = 6})
        ' Col 1: ▶ przycisk odtwarzania
        Dim colPlay As New DataGridViewButtonColumn With {
            .HeaderText = "", .Width = 38, .FillWeight = 6,
            .Text = ChrW(&H25B6), .UseColumnTextForButtonValue = True,
            .DefaultCellStyle = New DataGridViewCellStyle With {
                .ForeColor = Color.FromArgb(0, 140, 0),
                .Font = New Font("Segoe UI", 9, FontStyle.Bold),
                .Padding = New Padding(0)
            }
        }
        dgv.Columns.Add(colPlay)
        ' Col 2: Nazwa  Col 3: URL
        dgv.Columns.Add(New DataGridViewTextBoxColumn With {.HeaderText = translations(currentLanguage)("col_station_name"), .ReadOnly = True, .FillWeight = 44})
        dgv.Columns.Add(New DataGridViewTextBoxColumn With {.HeaderText = translations(currentLanguage)("col_station_url"), .ReadOnly = True, .FillWeight = 44})
        ' Pomocnik: pobiera (URL, Name) wiersza o danym indeksie.
        Dim getRowData As Func(Of Integer, (URL As String, Name As String)) =
            Function(rowIndex As Integer)
                If rowIndex < 0 OrElse rowIndex >= dgv.Rows.Count Then Return (Nothing, Nothing)
                Dim row = dgv.Rows(rowIndex)
                Return (If(row.Cells(3).Value?.ToString(), Nothing),
                        If(row.Cells(2).Value?.ToString(), Nothing))
            End Function

        ' Klik na przycisk ▶ (kolumna 1) → odtwórz przez wbudowany odtwarzacz.
        AddHandler dgv.CellClick, Sub(s, args)
                                      If args.RowIndex < 0 OrElse args.ColumnIndex <> 1 Then Return
                                      Dim d = getRowData(args.RowIndex)
                                      If d.URL IsNot Nothing Then PlayStationWmp(d.URL, If(d.Name, ""))
                                  End Sub

        ' Podwójny klik na nazwie/URL → odtwórz; na checkbox → zaznacz/odznacz.
        AddHandler dgv.CellDoubleClick, Sub(s, args)
                                            If args.RowIndex < 0 Then Return
                                            If args.ColumnIndex = 0 Then
                                                Dim cur As Boolean = Convert.ToBoolean(dgv.Rows(args.RowIndex).Cells(0).Value)
                                                dgv.Rows(args.RowIndex).Cells(0).Value = Not cur
                                            ElseIf args.ColumnIndex >= 2 Then
                                                Dim d = getRowData(args.RowIndex)
                                                If d.URL IsNot Nothing Then PlayStationWmp(d.URL, If(d.Name, ""))
                                            End If
                                        End Sub
        outDgv = dgv
        selStationsForm.Controls.Add(dgv)

        ' Dolny pasek przycisków.
        Dim panelButtons As New Panel With {.Dock = DockStyle.Bottom, .Height = 54, .BackColor = ThemeBg}
        Dim lblCount As New Label With {.Text = translations(currentLanguage)("lbl_checking"), .AutoSize = True, .Left = 12, .Top = 18, .ForeColor = ThemeText, .Font = New Font("Segoe UI", 9, FontStyle.Bold)}
        outLblCount = lblCount
        Dim lblStatus As New Label With {.Text = "", .AutoSize = True, .Left = 12, .Top = 18, .ForeColor = ThemeAccent, .Font = New Font("Segoe UI", 9, FontStyle.Bold), .Visible = False}
        outLblStatus = lblStatus
        Dim btnSelectAll As New Button With {.Text = translations(currentLanguage)("btn_select_all"), .Width = 130, .Height = 34, .Top = 9}
        StyleButton(btnSelectAll)
        AddHandler btnSelectAll.Click, Sub()
                                           For Each row As DataGridViewRow In dgv.Rows
                                               row.Cells(0).Value = True
                                           Next
                                       End Sub
        Dim btnAdd As New Button With {.Text = translations(currentLanguage)("btn_add"), .Width = 120, .Height = 34, .Top = 9}
        StyleAccentButton(btnAdd)
        AddHandler btnAdd.Click, Sub()
                                     Dim addedCount As Integer = 0
                                     For Each row As DataGridViewRow In dgv.Rows
                                         If Convert.ToBoolean(row.Cells(0).Value) = True Then
                                             stations.Add(New Station With {
                                                 .Name = row.Cells(2).Value.ToString(),
                                                 .URL = row.Cells(3).Value.ToString(),
                                                 .Volume = "0"
                                             })
                                             addedCount += 1
                                             row.Cells(0).Value = False   ' odznacz po dodaniu
                                         End If
                                     Next
                                     If addedCount = 0 Then Return  ' nic nie zaznaczone
                                     stations = stations.GroupBy(Function(s) s.URL).Select(Function(g) g.First()).ToList()
                                     stations.Sort(Function(a, b) String.Compare(a.Name, b.Name, True))
                                     UpdateDataGridView()
                                     AppendLog(String.Format(translations(currentLanguage)("log_stations_added"), stations.Count))
                                     ' Nie zamykamy okna — można dalej wybierać stacje
                                     lblCount.Text = String.Format(translations(currentLanguage)("lbl_added_total"), addedCount, stations.Count)
                                 End Sub
        Dim btnCancel As New Button With {.Text = translations(currentLanguage)("btn_close"), .Width = 110, .Height = 34, .Top = 9}
        StyleButton(btnCancel)
        AddHandler btnCancel.Click, Sub() selStationsForm.Close()
        Dim btnRecheck As New Button With {.Text = translations(currentLanguage)("btn_recheck"), .Width = 140, .Height = 34, .Top = 9, .Visible = recheckCallback IsNot Nothing}
        StyleButton(btnRecheck)
        AddHandler btnRecheck.Click,
            Sub()
                If recheckCallback Is Nothing Then Return
                btnRecheck.Enabled = False
                btnRecheck.Text = translations(currentLanguage)("btn_rechecking")
                Task.Run(Async Function()
                             Try
                                 Await recheckCallback()
                             Finally
                                 Try
                                     btnRecheck.BeginInvoke(Sub()
                                                                btnRecheck.Enabled = True
                                                                btnRecheck.Text = translations(currentLanguage)("btn_recheck")
                                                            End Sub)
                                 Catch
                                 End Try
                             End Try
                         End Function)
            End Sub
        Dim btnPlay As New Button With {.Text = translations(currentLanguage)("btn_play"), .Width = 100, .Height = 34, .Top = 9, .Enabled = False}
        StyleButton(btnPlay)
        btnPlay.ForeColor = Color.FromArgb(0, 140, 0)
        btnPlay.Font = New Font("Segoe UI", 9.5!, FontStyle.Bold)
        AddHandler btnPlay.Click, Sub()
                                      Dim rowIdx = If(dgv.SelectedRows.Count > 0, dgv.SelectedRows(0).Index,
                                                      If(dgv.CurrentRow IsNot Nothing, dgv.CurrentRow.Index, -1))
                                      Dim d = getRowData(rowIdx)
                                      If d.URL IsNot Nothing Then PlayStationWmp(d.URL, If(d.Name, ""))
                                  End Sub
        ' Włącz Play gdy jest zaznaczony wiersz.
        AddHandler dgv.SelectionChanged, Sub()
                                             btnPlay.Enabled = dgv.SelectedRows.Count > 0 OrElse dgv.CurrentRow IsNot Nothing
                                         End Sub
        AddHandler panelButtons.Resize, Sub() LayoutPickerButtons(panelButtons, btnAdd, btnCancel, btnSelectAll, btnRecheck, btnPlay)
        panelButtons.Controls.AddRange({lblCount, btnPlay, btnRecheck, btnSelectAll, btnAdd, btnCancel})
        selStationsForm.Controls.Add(panelButtons)

        ' ── Pasek odtwarzacza ──────────────────────────────────────────────────────
        ' Dwa wiersze: górny = status (kolorowy) + bitrate, dolny = nazwa stacji.
        Dim pnlPlayer As New Panel With {.Dock = DockStyle.Bottom, .Height = 66,
            .BackColor = Color.FromArgb(22, 22, 28)}

        ' --- Przyciski ▶ i ■ ---
        Dim btnWmpPlay As New Button With {
            .Text = ChrW(&H25B6), .Width = 44, .Height = 56, .Left = 4, .Top = 5,
            .FlatStyle = FlatStyle.Flat, .BackColor = Color.FromArgb(0, 130, 60),
            .ForeColor = Color.White, .Font = New Font("Segoe UI", 13, FontStyle.Bold),
            .Cursor = Cursors.Hand}
        btnWmpPlay.FlatAppearance.BorderSize = 0
        btnWmpPlay.FlatAppearance.MouseOverBackColor = Color.FromArgb(0, 170, 80)

        Dim btnWmpStop As New Button With {
            .Text = ChrW(&H25A0), .Width = 40, .Height = 56, .Left = 52, .Top = 5,
            .FlatStyle = FlatStyle.Flat, .BackColor = Color.FromArgb(160, 35, 35),
            .ForeColor = Color.White, .Font = New Font("Segoe UI", 11, FontStyle.Bold),
            .Cursor = Cursors.Hand}
        btnWmpStop.FlatAppearance.BorderSize = 0
        btnWmpStop.FlatAppearance.MouseOverBackColor = Color.FromArgb(200, 50, 50)

        ' Layout paska:
        ' [▶][■]  ● GRA  128k      Sunshine Live          [VOL]
        ' Kropka (12x12) w pionie wyśrodkowana, za nią status + bitrate w jednym wierszu,
        ' pod spodem (wiersz 2) — nazwa stacji.
        ' Wszystkie etykiety zaczynają się od Left=120 — bez nakładania na kropkę (100+12=112).

        Dim dotSize = 12
        Dim lblDot As New Label With {
            .Width = dotSize, .Height = dotSize, .Left = 100, .Top = 9,
            .BackColor = Color.FromArgb(100, 100, 100)}
        Dim dotPath As New Drawing2D.GraphicsPath()
        dotPath.AddEllipse(0, 0, dotSize, dotSize)
        lblDot.Region = New Region(dotPath)

        ' Wiersz 1: "GRA  128k" — status + bitrate obok siebie w jednym labelu
        ' (bitrate jako część tekstu statusu — brak ryzyka nakładania)
        Dim lblPlayerStatus As New Label With {
            .Text = "STOP", .Left = 120, .Top = 4, .Height = 20,
            .ForeColor = Color.FromArgb(160, 160, 160),
            .Font = New Font("Segoe UI", 8, FontStyle.Bold),
            .AutoSize = False, .Anchor = AnchorStyles.Left Or AnchorStyles.Right Or AnchorStyles.Top}

        ' Wiersz 2: pełna nazwa stacji
        Dim lblNowPlay As New Label With {
            .Text = "", .Left = 120, .Top = 27, .Height = 20,
            .ForeColor = Color.White,
            .Font = New Font("Segoe UI", 9.5!, FontStyle.Regular),
            .AutoSize = False, .Anchor = AnchorStyles.Left Or AnchorStyles.Right Or AnchorStyles.Top}

        ' Bitrate jako osobna etykieta NIE ISTNIEJE — wbudowany w lblPlayerStatus ("GRA  128k")
        Dim lblBitrate As New Label With {
            .Visible = False, .Width = 0, .Height = 0}  ' placeholder — nie renderowany

        ' --- Suwak głośności ---
        Dim lblVol As New Label With {
            .Text = "VOL", .Left = 0, .Top = 50, .Width = 30, .Height = 14,
            .ForeColor = Color.FromArgb(120, 120, 120),
            .Font = New Font("Segoe UI", 7), .TextAlign = ContentAlignment.MiddleCenter,
            .Anchor = AnchorStyles.Right Or AnchorStyles.Bottom}
        Dim trkVol As New TrackBar With {
            .Minimum = 0, .Maximum = 100, .Value = 80, .Width = 130, .Height = 26,
            .Top = 38, .TickFrequency = 25,
            .Anchor = AnchorStyles.Right Or AnchorStyles.Bottom,
            .BackColor = Color.FromArgb(22, 22, 28)}

        ' Przypisz referencje do pól klasy (timer aktualizuje te kontrolki)
        _wmpNowPlayingLbl = lblNowPlay
        _wmpStatusLbl = lblPlayerStatus
        _wmpStatusDot = lblDot
        _wmpBitrateLbl = lblBitrate
        _wmpPlayPauseBtn = btnWmpPlay

        AddHandler btnWmpPlay.Click,
            Sub()
                Dim rowIdx2 = If(dgv.SelectedRows.Count > 0, dgv.SelectedRows(0).Index,
                                 If(dgv.CurrentRow IsNot Nothing, dgv.CurrentRow.Index, -1))
                Dim d2 = getRowData(rowIdx2)
                If d2.URL IsNot Nothing Then
                    PlayStationWmp(d2.URL, If(d2.Name, ""))
                Else
                    TogglePauseWmp()
                End If
            End Sub
        AddHandler btnWmpStop.Click, Sub() StopPlayerWmp()
        AddHandler trkVol.Scroll, Sub() SetVolumeWmp(trkVol.Value)
        AddHandler pnlPlayer.Resize,
            Sub()
                Dim w = pnlPlayer.Width
                Dim rightW = trkVol.Width + 8
                lblPlayerStatus.Width = w - 120 - rightW - 4
                lblNowPlay.Width = w - 120 - rightW - 4
                trkVol.Left = w - trkVol.Width - 4
                lblVol.Left = w - trkVol.Width - 4
            End Sub

        pnlPlayer.Controls.AddRange({btnWmpPlay, btnWmpStop, lblDot, lblBitrate, lblPlayerStatus, lblNowPlay, trkVol, lblVol})
        selStationsForm.Controls.Add(pnlPlayer)

        ' Górny pasek z polem filtra na żywo.
        Dim topPanel As New Panel With {.Dock = DockStyle.Top, .Height = 46, .BackColor = ThemeBg}
        Dim lblFilter As New Label With {.Text = translations(currentLanguage)("lbl_filter"), .AutoSize = True, .Left = 12, .Top = 14, .ForeColor = ThemeText, .Font = New Font("Segoe UI", 9)}
        Dim txtFilter As New TextBox With {.Left = 120, .Top = 10, .Width = 660, .Font = New Font("Segoe UI", 10), .Anchor = AnchorStyles.Left Or AnchorStyles.Right Or AnchorStyles.Top}
        topPanel.Controls.AddRange({lblFilter, txtFilter})
        selStationsForm.Controls.Add(topPanel)

        ' Gdy zmienia się filtr → odbuduj tabelę z pickerAllStations.
        AddHandler txtFilter.TextChanged,
            Sub()
                _pickerFilter = txtFilter.Text.Trim()
                Dim currentFilter = _pickerFilter
                Dim snapshot As List(Of Station)
                SyncLock pickerAllStations
                    snapshot = pickerAllStations.ToList()
                End SyncLock
                Dim matched = If(currentFilter = "", snapshot,
                    snapshot.Where(Function(s) s.Name.IndexOf(currentFilter, StringComparison.OrdinalIgnoreCase) >= 0 OrElse s.URL.IndexOf(currentFilter, StringComparison.OrdinalIgnoreCase) >= 0).ToList())
                dgv.SuspendLayout()
                dgv.Rows.Clear()
                If matched.Count > 0 Then
                    Dim rows(matched.Count - 1) As DataGridViewRow
                    For i = 0 To matched.Count - 1
                        Dim r As New DataGridViewRow()
                        r.CreateCells(dgv, False, Nothing, matched(i).Name, matched(i).URL)
                        rows(i) = r
                    Next
                    dgv.Rows.AddRange(rows)
                End If
                dgv.ResumeLayout()
                lblCount.Text = String.Format(translations(currentLanguage)("lbl_found_working"), dgv.Rows.Count)
            End Sub

        LayoutPickerButtons(panelButtons, btnAdd, btnCancel, btnSelectAll)
        Return selStationsForm
    End Function

    Private Sub LayoutPickerButtons(panel As Panel, btnAdd As Button, btnCancel As Button, btnSelectAll As Button,
                                    Optional btnRecheck As Button = Nothing, Optional btnPlay As Button = Nothing)
        btnCancel.Left = panel.Width - btnCancel.Width - 10
        btnAdd.Left = btnCancel.Left - btnAdd.Width - 8
        btnSelectAll.Left = btnAdd.Left - btnSelectAll.Width - 8
        If btnRecheck IsNot Nothing Then btnRecheck.Left = btnSelectAll.Left - btnRecheck.Width - 8
        If btnPlay IsNot Nothing Then btnPlay.Left = If(btnRecheck IsNot Nothing, btnRecheck.Left - btnPlay.Width - 8, btnSelectAll.Left - btnPlay.Width - 8)
    End Sub

    ' Otwiera strumień w domyślnym odtwarzaczu systemu (VLC, WMP, itp.).
    Private Sub PlayStreamUrl(url As String)
        If String.IsNullOrWhiteSpace(url) Then Return
        Try
            Process.Start(New ProcessStartInfo With {
                .FileName = url,
                .UseShellExecute = True
            })
        Catch ex As Exception
            AppendLog("Blad otwierania strumienia: " & ex.Message)
        End Try
    End Sub
    ' onWorking: wywoływane dla każdej działającej stacji na żywo (może być Nothing).
    Private Async Function CheckStationsAsync(stationsList As List(Of Station),
                                              Optional updateOutputFile As Boolean = True,
                                              Optional onWorking As Action(Of Station) = Nothing) As Task(Of List(Of Station))
        Me.Invoke(Sub()
                      btnSelectFile.Enabled = False : btnSelectCountry.Enabled = False
                      btnDownloadFromRadio.Enabled = False : btnReset.Enabled = False
                      btnEditList.Enabled = False : btnSave.Enabled = False
                      btnSendToRadio.Enabled = False : btnBuyCoffee.Enabled = False
                      btnSearchRadioAgain.Enabled = False
                  End Sub)
        Try
            Dim totalCount As Integer = stationsList.Count
            totalStationsCount = totalCount
            checkedStationsCount = 0
            _statException = 0 : _statNonSuccess = 0 : _statMime = 0
            _statOkIcy = 0 : _statOkMime = 0 : _statOkTcp = 0

            ' Wzorzec "stałych workerów" zamiast task-per-station:
            ' tworzymy MAX_PARALLEL workerów, każdy pobiera stacje z ConcurrentQueue.
            ' Unikamy tworzenia 30k tasków i SemaphoreSlim — drastycznie mniejszy narzut.
            Dim queue As New Concurrent.ConcurrentQueue(Of Station)(stationsList)
            Dim workingStations As New Concurrent.ConcurrentBag(Of Station)
            Dim lastReportedPercent As Integer = -1
            Dim progressFmt As String = translations(currentLanguage)("lbl_progress_initial").Replace("0/0", "{0}/{1}")

            Dim workers(MAX_PARALLEL - 1) As Task
            For i = 0 To MAX_PARALLEL - 1
                workers(i) = Task.Run(Async Function()
                                          Dim st As Station = Nothing
                                          While queue.TryDequeue(st)
                                              Dim result = Await CheckStationAsync(st)
                                              If result.Item2 Then
                                                  workingStations.Add(result.Item1) ' finalUrl może być inny
                                                  onWorking?.Invoke(result.Item1)
                                              End If
                                              Dim cnt = Interlocked.Increment(checkedStationsCount)
                                              Dim pct = CInt((cnt / totalCount) * 100)
                                              If pct <> lastReportedPercent Then
                                                  lastReportedPercent = pct
                                                  Try
                                                      progressBar.BeginInvoke(Sub()
                                                                                  progressBar.Value = Math.Min(pct, 100)
                                                                                  lblProgress.Text = String.Format(progressFmt, cnt, totalCount)
                                                                              End Sub)
                                                  Catch
                                                  End Try
                                              End If
                                          End While
                                      End Function)
            Next
            Await Task.WhenAll(workers)

            ' Podsumowanie diagnostyczne — pomaga zrozumieć co jest odrzucane i dlaczego.
            Dim working2 = workingStations.Count
            AppendLog($"[DIAG] Wynik: {working2} dzialajacych / {stationsList.Count} total")
            AppendLog($"[DIAG] Zaakceptowane: ICY={_statOkIcy} MIME={_statOkMime} TCP={_statOkTcp}")
            AppendLog($"[DIAG] Odrzucone: exception={_statException} non2xx={_statNonSuccess} zly_mime={_statMime}")

            Dim result2 = workingStations.ToList()
            result2.Sort(Function(a, b) String.Compare(a.Name, b.Name, True))
            Return result2
        Finally
            Me.Invoke(Sub()
                          btnSelectFile.Enabled = True : btnSelectCountry.Enabled = True
                          btnDownloadFromRadio.Enabled = True : btnReset.Enabled = True
                          btnEditList.Enabled = True : btnSave.Enabled = True
                          btnSendToRadio.Enabled = True : btnBuyCoffee.Enabled = True
                          btnSearchRadioAgain.Enabled = True
                      End Sub)
        End Try
    End Function
    Private Async Function CheckStationAsync(st As Station) As Task(Of (Station, Boolean, String))
        Dim url = st.URL.Trim()
        If String.IsNullOrEmpty(url) Then Return (st, False, "brak URL")
        ' Cache — nie sprawdzaj tego samego URL dwa razy.
        Dim cached As (Boolean, String)
        If stationCache.TryGetValue(url, cached) Then Return (st, cached.Item1, cached.Item2)
        ' Bezpośrednie wywołanie bez tworzenia CTS/List/Task.WhenAny per stacja.
        ' 99% stacji katalogowych ma pełny http/https — jeden URL, jedno żądanie.
        Dim r = Await CheckStreamUrlAdvancedAsync(st, url, CancellationToken.None)
        ' Cachujemy tylko pozytywne — martwe stacje mogą ożyć i powinny być sprawdzane ponownie.
        If r.Item2 Then stationCache(url) = (True, "")
        Return r
    End Function
    ' Raw TCP check — obsługuje ICY/1.0 protocol (stare Shoutcast v1) i serwery których
    ' HttpClient nie może sparsować (błędy nagłówków, niestandardowy protokół).
    ' Wysyła surowe żądanie GET i sprawdza czy odpowiedź zaczyna się od ICY 200 lub HTTP 2xx.
    Private Async Function TcpCheckAsync(url As String) As Task(Of Boolean)
        Try
            Dim uri As New Uri(url)
            Dim host = uri.Host
            Dim port = If(uri.IsDefaultPort, 80, uri.Port)
            Dim path = If(String.IsNullOrEmpty(uri.PathAndQuery), "/", uri.PathAndQuery)
            Using tcp As New System.Net.Sockets.TcpClient()
                Dim connTask = tcp.ConnectAsync(host, port)
                If Await Task.WhenAny(connTask, Task.Delay(3000)) IsNot connTask OrElse connTask.IsFaulted Then Return False
                Using ns As System.Net.Sockets.NetworkStream = tcp.GetStream()
                    ns.ReadTimeout = 3000 : ns.WriteTimeout = 3000
                    Dim req = "GET " & path & " HTTP/1.0" & vbCrLf &
                              "Host: " & host & vbCrLf &
                              "User-Agent: WinampMPEG/5.66" & vbCrLf &
                              "Icy-MetaData: 0" & vbCrLf & vbCrLf
                    Dim reqBytes = Encoding.ASCII.GetBytes(req)
                    ns.Write(reqBytes, 0, reqBytes.Length)
                    Dim buf(511) As Byte
                    Dim rTask = ns.ReadAsync(buf, 0, buf.Length)
                    If Await Task.WhenAny(rTask, Task.Delay(3000)) IsNot rTask OrElse rTask.IsFaulted Then Return False
                    Dim n = rTask.Result
                    If n < 4 Then Return False
                    Dim firstLine = Encoding.ASCII.GetString(buf, 0, Math.Min(n, 50))
                    Return firstLine.StartsWith("ICY 2", StringComparison.OrdinalIgnoreCase) OrElse
                           (firstLine.StartsWith("HTTP") AndAlso
                            (firstLine.Contains(" 200 ") OrElse firstLine.Contains(" 206 ")))
                End Using
            End Using
        Catch
            Return False
        End Try
    End Function

    ' Sprawdza nagłówki ICY (Icecast/Shoutcast) — obecność icy-* = na pewno strumień audio,
    ' nawet jeśli Content-Type jest błędnie ustawiony na text/html.
    Private Shared Function HasIcyHeaders(response As HttpResponseMessage) As Boolean
        Return response.Headers.Any(Function(h) h.Key.StartsWith("icy-", StringComparison.OrdinalIgnoreCase))
    End Function

    ' Odrzucamy TYLKO jawne typy nie-audio (HTML, JSON, XML).
    ' Wcześniej biała lista była za wąska i odrzucała poprawne strumienie.
    ' Teraz: jeśli nie jest to na pewno strona webowa — przepuszczamy.
    Private Shared Function IsAudioMime(ct As String) As Boolean
        If String.IsNullOrEmpty(ct) Then Return True  ' brak Content-Type = przepuść
        ct = ct.ToLowerInvariant()
        Return Not (ct.StartsWith("text/html") OrElse ct.StartsWith("text/plain") OrElse
                    ct.StartsWith("application/json") OrElse ct.StartsWith("application/xml") OrElse
                    ct.StartsWith("text/xml") OrElse ct.StartsWith("text/javascript"))
    End Function

    Private Async Function CheckStreamUrlAdvancedAsync(st As Station, url As String,
                                                   ct As CancellationToken,
                                                   Optional maxRedirects As Integer = 5) As Task(Of (Station, Boolean, String))
        Dim lower = url.ToLowerInvariant()
        If lower.StartsWith("mms://") OrElse lower.StartsWith("rtsp://") OrElse lower.StartsWith("icecast://") Then
            Return (st, False, "nieobslugiwany protokol")
        End If
        Dim currentUrl = url
        Dim redirectCount = 0
        Dim useGet = False
        Dim doTcpCheck = False   ' flaga — oba (HEAD i GET) rzuciły wyjątek, sprawdź TcpClient
        While redirectCount <= maxRedirects
            doTcpCheck = False
            ' HEAD: 2s (powinien odpowiedzieć natychmiast)
            ' GET:  4s (może potrzebować więcej czasu na pierwsze bajty)
            Dim timeoutSec = If(useGet, 4, 2)
            Dim reqCts As New CancellationTokenSource(TimeSpan.FromSeconds(timeoutSec))
            Try
                Dim method = If(useGet, HttpMethod.Get, HttpMethod.Head)
                Dim req As New HttpRequestMessage(method, currentUrl)
                req.Headers.TryAddWithoutValidation("User-Agent", "WinampMPEG/5.66")
                req.Headers.TryAddWithoutValidation("Icy-MetaData", "0")
                Dim response = Await streamCheckClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, reqCts.Token)
                Dim code = CInt(response.StatusCode)
                ' 405 = HEAD nieobsługiwany → przełącz na GET
                If code = 405 AndAlso Not useGet Then
                    useGet = True : Continue While
                End If
                If response.IsSuccessStatusCode Then
                    If HasIcyHeaders(response) Then
                        Interlocked.Increment(_statOkIcy)
                        Return (New Station With {.URL = response.RequestMessage.RequestUri.ToString(), .Name = st.Name}, True, "OK")
                    End If
                    Dim mime = response.Content?.Headers?.ContentType?.MediaType
                    If IsAudioMime(mime) Then
                        Interlocked.Increment(_statOkMime)
                        Return (New Station With {.URL = response.RequestMessage.RequestUri.ToString(), .Name = st.Name}, True, "OK")
                    ElseIf Not useGet Then
                        useGet = True : Continue While
                    Else
                        Interlocked.Increment(_statMime)
                        Return (st, False, "nie dziala")
                    End If
                ElseIf code >= 300 AndAlso code < 400 Then
                    Dim loc = response.Headers.Location?.ToString()
                    If String.IsNullOrEmpty(loc) Then
                        Interlocked.Increment(_statNonSuccess)
                        Return (st, False, "nie dziala")
                    End If
                    If Not loc.StartsWith("http") Then loc = New Uri(New Uri(currentUrl), loc).ToString()
                    currentUrl = loc
                    redirectCount += 1
                    useGet = False
                Else
                    Interlocked.Increment(_statNonSuccess)
                    Return (st, False, "nie dziala")
                End If
            Catch
                ' VB.NET nie pozwala Await w Catch — używamy flagi i sprawdzamy po End Try.
                If Not useGet Then
                    useGet = True : Continue While
                End If
                ' Oba (HEAD i GET) rzuciły wyjątek — flaga do sprawdzenia po End Try.
                doTcpCheck = True
            Finally
                reqCts.Dispose()
            End Try
            ' doTcpCheck=True: oba HEAD i GET rzuciły wyjątek — sprawdź raw socket
            ' (np. ICY/1.0 protocol, niestandardowe nagłówki których HttpClient nie może sparsować)
            If doTcpCheck Then
                If Await TcpCheckAsync(url) Then
                    Interlocked.Increment(_statOkTcp)
                    Return (New Station With {.URL = url, .Name = st.Name}, True, "OK-TCP")
                End If
                Interlocked.Increment(_statException)
                Return (st, False, "nie dziala")
            End If
        End While
        Return (st, False, "nie dziala")
    End Function
    Private Sub UpdateDataGridView(Optional stationsList As List(Of Station) = Nothing)
        If stationsList Is Nothing Then stationsList = stations
        If dgvStations.InvokeRequired Then
            dgvStations.Invoke(Sub()
                                   dgvStations.Rows.Clear()
                                   For Each st In stationsList
                                       dgvStations.Rows.Add(st.Name, st.URL, st.Volume)
                                   Next
                               End Sub)
        Else
            dgvStations.Rows.Clear()
            For Each st In stationsList
                dgvStations.Rows.Add(st.Name, st.URL, st.Volume)
            Next
        End If
    End Sub
    Private Sub btnSave_Click(sender As Object, e As EventArgs)
        Dim outputFile As String = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "output_stations.csv")
        Try
            Using writer As New StreamWriter(outputFile, False)
                For Each st In stations
                    writer.WriteLine($"{st.Name}{vbTab}{st.URL}{vbTab}{st.Volume}")
                Next
            End Using
            AppendLog(String.Format(translations(currentLanguage)("log_file_saved"), stations.Count, outputFile))
            MessageBox.Show(String.Format(translations(currentLanguage)("msg_file_saved"), outputFile), translations(currentLanguage)("msg_success_title"), MessageBoxButtons.OK, MessageBoxIcon.Information)
        Catch ex As Exception
            MessageBox.Show(String.Format(translations(currentLanguage)("msg_file_save_error"), ex.Message), translations(currentLanguage)("msg_error_title"), MessageBoxButtons.OK, MessageBoxIcon.Error)
        End Try
    End Sub
    Private Sub btnEditList_Click(sender As Object, e As EventArgs)
        If stations.Count = 0 Then
            MessageBox.Show(translations(currentLanguage)("msg_empty_stations_list"), translations(currentLanguage)("msg_error_title"), MessageBoxButtons.OK, MessageBoxIcon.Information)
            Return
        End If
        Dim editForm As New Form With {
        .Text = translations(currentLanguage)("edit_list_title"),
        .Size = New Size(600, 450),
        .FormBorderStyle = FormBorderStyle.FixedDialog,
        .MaximizeBox = False,
        .MinimizeBox = False,
        .StartPosition = FormStartPosition.CenterParent
    }
        Dim dgv As New DataGridView With {
        .Dock = DockStyle.Top,
        .RowHeadersVisible = False,
        .Height = 320,
        .AllowUserToAddRows = False,
        .AllowUserToDeleteRows = False,
        .ReadOnly = False,
        .SelectionMode = DataGridViewSelectionMode.FullRowSelect,
        .Font = New Font("Segoe UI", 10),
        .AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill
    }
        Dim colCheck As New DataGridViewCheckBoxColumn With {
        .HeaderText = "",
        .Width = 40,
        .FillWeight = 10
    }
        Dim colName As New DataGridViewTextBoxColumn With {
        .HeaderText = translations(currentLanguage)("col_station_name"),
        .FillWeight = 40
    }
        Dim colUrl As New DataGridViewTextBoxColumn With {
        .HeaderText = translations(currentLanguage)("col_station_url"),
        .FillWeight = 40
    }
        Dim colVolume As New DataGridViewTextBoxColumn With {
        .HeaderText = "Volume",
        .FillWeight = 10
    }
        dgv.Columns.Add(colCheck)
        dgv.Columns.Add(colName)
        dgv.Columns.Add(colUrl)
        dgv.Columns.Add(colVolume)
        For Each st In stations
            dgv.Rows.Add(True, st.Name, st.URL, st.Volume)
        Next
        AddHandler dgv.CellDoubleClick, Sub(s, args)
                                            If args.RowIndex >= 0 Then
                                                Dim currentValue As Boolean = Convert.ToBoolean(dgv.Rows(args.RowIndex).Cells(0).Value)
                                                dgv.Rows(args.RowIndex).Cells(0).Value = Not currentValue
                                            End If
                                        End Sub
        editForm.Controls.Add(dgv)
        Dim bottomPanel As New Panel With {
        .Dock = DockStyle.Bottom,
        .Height = 100
    }
        Dim txtNewName As New TextBox With {
        .Text = translations(currentLanguage)("txt_new_station_name"),
        .ForeColor = Color.Gray,
        .Location = New Point(10, 10),
        .Width = 200
    }
        AddHandler txtNewName.GotFocus, Sub()
                                            If txtNewName.Text = translations(currentLanguage)("txt_new_station_name") Then
                                                txtNewName.Text = ""
                                                txtNewName.ForeColor = Color.Black
                                            End If
                                        End Sub
        AddHandler txtNewName.LostFocus, Sub()
                                             If String.IsNullOrWhiteSpace(txtNewName.Text) Then
                                                 txtNewName.Text = translations(currentLanguage)("txt_new_station_name")
                                                 txtNewName.ForeColor = Color.Gray
                                             End If
                                         End Sub
        bottomPanel.Controls.Add(txtNewName)
        Dim txtNewURL As New TextBox With {
        .Text = translations(currentLanguage)("txt_new_station_url"),
        .ForeColor = Color.Gray,
        .Location = New Point(220, 10),
        .Width = 300
    }
        AddHandler txtNewURL.GotFocus, Sub()
                                           If txtNewURL.Text = translations(currentLanguage)("txt_new_station_url") Then
                                               txtNewURL.Text = ""
                                               txtNewURL.ForeColor = Color.Black
                                           End If
                                       End Sub
        AddHandler txtNewURL.LostFocus, Sub()
                                            If String.IsNullOrWhiteSpace(txtNewURL.Text) Then
                                                txtNewURL.Text = translations(currentLanguage)("txt_new_station_url")
                                                txtNewURL.ForeColor = Color.Gray
                                            End If
                                        End Sub
        bottomPanel.Controls.Add(txtNewURL)
        Dim btnAdd As New Button With {
        .Text = translations(currentLanguage)("btn_add"),
        .Location = New Point(10, 50),
        .Width = 80,
        .Height = 30
    }
        AddHandler btnAdd.Click, Sub()
                                     If txtNewName.Text <> "" And txtNewName.Text <> translations(currentLanguage)("txt_new_station_name") And
                                    txtNewURL.Text <> "" And txtNewURL.Text <> translations(currentLanguage)("txt_new_station_url") Then
                                         dgv.Rows.Add(True, txtNewName.Text, txtNewURL.Text, "0")
                                         txtNewName.Text = translations(currentLanguage)("txt_new_station_name")
                                         txtNewName.ForeColor = Color.Gray
                                         txtNewURL.Text = translations(currentLanguage)("txt_new_station_url")
                                         txtNewURL.ForeColor = Color.Gray
                                     End If
                                 End Sub
        bottomPanel.Controls.Add(btnAdd)
        Dim btnSaveChanges As New Button With {
        .Text = translations(currentLanguage)("btn_save_changes"),
        .Location = New Point(100, 50),
        .Width = 120,
        .Height = 30
    }
        AddHandler btnSaveChanges.Click, Sub()
                                             Dim newList As New List(Of Station)
                                             For Each row As DataGridViewRow In dgv.Rows
                                                 If Not row.IsNewRow AndAlso Convert.ToBoolean(row.Cells(0).Value) Then
                                                     newList.Add(New Station With {
                    .Name = If(row.Cells(1).Value IsNot Nothing, row.Cells(1).Value.ToString(), ""),
                    .URL = If(row.Cells(2).Value IsNot Nothing, row.Cells(2).Value.ToString(), ""),
                    .Volume = If(row.Cells(3).Value IsNot Nothing, row.Cells(3).Value.ToString(), "0")
                })
                                                 End If
                                             Next
                                             stations = newList
                                             UpdateDataGridView()
                                             AppendLog(translations(currentLanguage)("log_list_updated"))
                                             editForm.Close()
                                         End Sub
        bottomPanel.Controls.Add(btnSaveChanges)
        Dim btnCancel As New Button With {
        .Text = translations(currentLanguage)("btn_cancel"),
        .Location = New Point(230, 50),
        .Width = 80,
        .Height = 30
    }
        AddHandler btnCancel.Click, Sub() editForm.Close()
        bottomPanel.Controls.Add(btnCancel)
        editForm.Controls.Add(bottomPanel)
        editForm.ShowDialog()
    End Sub

    Public Class Station
        Public Property Name As String
        Public Property URL As String
        Public Property Volume As String
    End Class
End Class