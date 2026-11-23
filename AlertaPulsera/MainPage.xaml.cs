using Plugin.BLE;
using Plugin.BLE.Abstractions;
using Plugin.BLE.Abstractions.Contracts;
using Plugin.BLE.Abstractions.EventArgs;
using Plugin.Firebase.CloudMessaging;
using Plugin.Firebase.Auth;
using Plugin.Firebase.Firestore;
using MQTTnet;
using MQTTnet.Client;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;


namespace AlertaPulsera;
public partial class MainPage : ContentPage
{
    private IAdapter? _adapter;
    private readonly HashSet<Guid> _dispositivosVistos = new();
    private bool _buscando;
    private bool _paginaVisible;

    private CancellationTokenSource? _cancelacionPulsos;

    private static readonly HttpClient HttpPulsos = new()
    {
        Timeout = Timeout.InfiniteTimeSpan
    };

    private const string UrlBpm =
        "https://alertapulserapro-default-rtdb.firebaseio.com/" +
        "pulsera/estado/bpm.json";
    public MainPage()
    {
        InitializeComponent();
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();

        _paginaVisible = true;

        if (_cancelacionPulsos != null)
            return;

        var cancelacion = new CancellationTokenSource();

        _cancelacionPulsos = cancelacion;

        _ = EscucharPulsosAsync(cancelacion);
    }

    protected override async void OnDisappearing()
    {
        _paginaVisible = false;

        var cancelacion = _cancelacionPulsos;
        _cancelacionPulsos = null;

        cancelacion?.Cancel();

        base.OnDisappearing();

        try
        {
            if (_adapter?.IsScanning == true)
            {
                await _adapter.StopScanningForDevicesAsync();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(ex);
        }
    }
    private async void OnBuscarBluetoothClicked(
        object sender,
        EventArgs e)
    {
        if (_buscando)
            return;

        _buscando = true;
        BuscarBluetoothButton.IsEnabled = false;

        try
        {
            EstadoBluetoothLabel.Text = "Solicitando permisos...";

            var permiso = await Permissions
                .RequestAsync<PermisosBluetooth>();

            if (permiso != PermissionStatus.Granted)
            {
                EstadoBluetoothLabel.Text =
                    "Debes permitir el acceso a dispositivos cercanos " +
                    "o ubicación, según tu versión de Android.";
                return;
            }

            var bluetooth = CrossBluetoothLE.Current;

            if (bluetooth.State != BluetoothState.On)
            {
                EstadoBluetoothLabel.Text =
                    "Bluetooth no disponible o apagado. " +
                    "Actívalo en los ajustes del celular.";
                return;
            }

            _adapter = bluetooth.Adapter;
            _adapter.ScanTimeout = 10000;

            _dispositivosVistos.Clear();
            DispositivosLayout.Children.Clear();

            _adapter.DeviceDiscovered += OnDispositivoEncontrado;

            EstadoBluetoothLabel.Text =
                "Buscando dispositivos BLE durante 10 segundos...";

            await _adapter.StartScanningForDevicesAsync();

            if (_paginaVisible)
            {
                EstadoBluetoothLabel.Text =
                    $"Búsqueda terminada: " +
                    $"{_dispositivosVistos.Count} dispositivos encontrados.";
            }
        }
        catch (Exception ex)
        {
            EstadoBluetoothLabel.Text = "No se pudo buscar Bluetooth.";

            await DisplayAlert(
                "Bluetooth",
                ex.Message,
                "Aceptar");
        }
        finally
        {
            if (_adapter != null)
            {
                _adapter.DeviceDiscovered -= OnDispositivoEncontrado;
            }

            _buscando = false;
            BuscarBluetoothButton.IsEnabled = true;
        }
    }

    private void OnDispositivoEncontrado(
        object? sender,
        DeviceEventArgs e)
    {
        var dispositivo = e.Device;

        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (!_paginaVisible || !_buscando)
                return;

            if (!_dispositivosVistos.Add(dispositivo.Id))
                return;

            string nombre = string.IsNullOrWhiteSpace(dispositivo.Name)
                ? "Dispositivo sin nombre"
                : dispositivo.Name;

            DispositivosLayout.Children.Add(new Label
            {
                Text = $"{nombre}\n" +
                       $"ID: {dispositivo.Id}\n" +
                       $"Señal: {dispositivo.Rssi} dBm",
                FontSize = 14,
                Margin = new Thickness(0, 5)
            });
        });
    }

    private async void OnActivarClicked(object sender, EventArgs e)
    {
        if (sender is Button boton)
            boton.IsEnabled = false;

        try
        {
            EstadoLabel.Text = "Configurando...";

#if ANDROID
            if (OperatingSystem.IsAndroidVersionAtLeast(33))
            {
                var permiso = await Permissions
                    .RequestAsync<Permissions.PostNotifications>();

                if (permiso != PermissionStatus.Granted)
                {
                    EstadoLabel.Text =
                        "Debes permitir las notificaciones.";
                    return;
                }
            }
#endif

            EstadoLabel.Text = "Iniciando sesión...";

            var auth = CrossFirebaseAuth.Current;
            var usuario = auth.CurrentUser;

            if (usuario == null)
            {
                usuario = await auth.SignInAnonymouslyAsync();
            }

            if (usuario == null)
            {
                throw new InvalidOperationException(
                    "No se pudo iniciar la sesión.");
            }

            EstadoLabel.Text = "Obteniendo token...";

            var token = await CrossFirebaseCloudMessaging
                .Current.GetTokenAsync();

            if (string.IsNullOrWhiteSpace(token))
            {
                throw new InvalidOperationException(
                    "Firebase no devolvió un token.");
            }

            TokenEditor.Text = token;

            const string claveInstalacion = "pulsera_instalacion_id";

            var instalacionId = Preferences.Default.Get(
                claveInstalacion,
                string.Empty);

            if (string.IsNullOrWhiteSpace(instalacionId))
            {
                instalacionId = Guid.NewGuid().ToString("N");

                Preferences.Default.Set(
                    claveInstalacion,
                    instalacionId);
            }

            EstadoLabel.Text = "Guardando token en Firebase...";

            var datos = new Dictionary<object, object>
{
    { "token", token },
    { "plataforma", "android" },
    { "actualizadoUtc", DateTime.UtcNow.ToString("O") }
};

            var ruta =
                $"usuarios/{usuario.Uid}/dispositivos/{instalacionId}";

            await CrossFirebaseFirestore.Current
                .GetDocument(ruta)
                .SetDataAsync(datos);

            EstadoLabel.Text = "Token registrado correctamente.";

            await DisplayAlert(
                "Registro ejecutado",
                $"Revisa esta ruta en Firestore:\n\n{ruta}",
                "Aceptar");
        }
        catch (Exception ex)
        {
            EstadoLabel.Text = "No se pudo registrar el token.";

            await DisplayAlert(
                "Configuración",
                ex.Message,
                "Aceptar");
        }
        finally
        {
            if (sender is Button botonFinal)
                botonFinal.IsEnabled = true;
        }
    }

    private async void OnCopiarClicked(object sender, EventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(TokenEditor.Text))
        {
            await Clipboard.Default.SetTextAsync(TokenEditor.Text);

            await DisplayAlert(
                "Listo",
                "Token copiado.",
                "Aceptar");
        }
    }
    private bool EsEscuchaActual(
    CancellationTokenSource cancelacion)
    {
        return _paginaVisible &&
               ReferenceEquals(
                   _cancelacionPulsos,
                   cancelacion) &&
               !cancelacion.IsCancellationRequested;
    }

    private void ActualizarPantallaPulsos(
        CancellationTokenSource cancelacion,
        string? lectura,
        string estado)
    {
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (!EsEscuchaActual(cancelacion))
                return;

            if (lectura != null)
            {
                PulsosLabel.Text = lectura;
            }

            EstadoPulsosLabel.Text = estado;
        });
    }

    private async Task EscucharPulsosAsync(
        CancellationTokenSource cancelacion)
    {
        var ct = cancelacion.Token;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    ActualizarPantallaPulsos(
                        cancelacion,
                        "-- BPM",
                        "Conectando a pulsos...");

                    var auth = CrossFirebaseAuth.Current;

                    var usuario = auth.CurrentUser;

                    if (usuario == null)
                    {
                        usuario = await auth
                            .SignInAnonymouslyAsync();
                    }

                    ct.ThrowIfCancellationRequested();

                    if (usuario == null)
                    {
                        throw new InvalidOperationException(
                            "No se pudo iniciar sesión en Firebase.");
                    }

                    // Token de autenticación de Firebase.
                    // No es el token FCM de notificaciones.
                    var resultadoToken = await usuario.GetIdTokenResultAsync();
                    var token = resultadoToken.Token;

                    ct.ThrowIfCancellationRequested();

                    if (string.IsNullOrWhiteSpace(token))
                    {
                        throw new InvalidOperationException(
                            "No se obtuvo el token de autenticación.");
                    }

                    string url =
                        $"{UrlBpm}?auth={Uri.EscapeDataString(token)}";

                    using var solicitud = new HttpRequestMessage(
                        HttpMethod.Get,
                        url);

                    solicitud.Headers.Accept.Add(
                        new MediaTypeWithQualityHeaderValue(
                            "text/event-stream"));

                    using var respuesta = await HttpPulsos.SendAsync(
                        solicitud,
                        HttpCompletionOption.ResponseHeadersRead,
                        ct);

                    if (!respuesta.IsSuccessStatusCode)
                    {
                        throw new InvalidOperationException(
                            "Firebase no permitió leer los pulsos. " +
                            $"HTTP {(int)respuesta.StatusCode}. " +
                            "Revisa las reglas y la autenticación.");
                    }

                    ActualizarPantallaPulsos(
                        cancelacion,
                        null,
                        "Esperando lectura de la pulsera...");

                    using var flujo = await respuesta.Content
                        .ReadAsStreamAsync(ct);

                    using var lector = new StreamReader(flujo);

                    string evento = string.Empty;
                    var datos = new StringBuilder();

                    while (!ct.IsCancellationRequested)
                    {
                        var linea = await lector.ReadLineAsync(ct);

                        if (linea == null)
                        {
                            throw new IOException(
                                "La conexión de pulsos se cerró.");
                        }

                        if (linea.Length == 0)
                        {
                            if (evento == "cancel" ||
                                evento == "auth_revoked")
                            {
                                throw new InvalidOperationException(
                                    "La sesión o el permiso de lectura " +
                                    "de Firebase cambió.");
                            }

                            if ((evento == "put" ||
                                 evento == "patch") &&
                                datos.Length > 0)
                            {
                                MostrarEventoPulsos(
                                    cancelacion,
                                    datos.ToString());
                            }

                            evento = string.Empty;
                            datos.Clear();

                            continue;
                        }

                        if (linea.StartsWith(
                                "event:",
                                StringComparison.Ordinal))
                        {
                            evento = linea[6..].Trim();
                        }
                        else if (linea.StartsWith(
                                     "data:",
                                     StringComparison.Ordinal))
                        {
                            if (datos.Length > 0)
                            {
                                datos.Append('\n');
                            }

                            datos.Append(
                                linea[5..].TrimStart());
                        }
                    }
                }
                catch (OperationCanceledException)
                    when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // No registrar la URL: contiene el token de sesión.
                    System.Diagnostics.Debug.WriteLine(
                        $"[PULSOS] {ex.GetType().Name}");

                    ActualizarPantallaPulsos(
                        cancelacion,
                        "-- BPM",
                        "Sin conexión de pulsos. " +
                        "Reintentando en 5 segundos...");

                    await Task.Delay(
                        TimeSpan.FromSeconds(5),
                        ct);
                }
            }
        }
        catch (OperationCanceledException)
            when (ct.IsCancellationRequested)
        {
            // La pantalla dejó de estar visible.
        }
        finally
        {
            cancelacion.Dispose();
        }
    }

    private void MostrarEventoPulsos(
        CancellationTokenSource cancelacion,
        string json)
    {
        using var documento = JsonDocument.Parse(json);

        var raiz = documento.RootElement;

        if (!raiz.TryGetProperty("data", out var valor))
            return;

        if (valor.ValueKind == JsonValueKind.Null)
        {
            ActualizarPantallaPulsos(
                cancelacion,
                "-- BPM",
                "Esperando lectura de la pulsera...");

            return;
        }

        if (valor.ValueKind != JsonValueKind.Number ||
            !valor.TryGetDouble(out double bpm) ||
            !double.IsFinite(bpm) ||
            bpm <= 0)
        {
            ActualizarPantallaPulsos(
                cancelacion,
                "-- BPM",
                "Lectura de pulsos no válida.");

            return;
        }

        ActualizarPantallaPulsos(
            cancelacion,
            $"{bpm:0.#} BPM",
            string.Empty);
    }


}