using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Storage;

using Plugin.BLE;
using Plugin.BLE.Abstractions;
using Plugin.BLE.Abstractions.Contracts;
using Plugin.BLE.Abstractions.EventArgs;

using Plugin.Firebase.Auth;
using Plugin.Firebase.CloudMessaging;
using Plugin.Firebase.Firestore;

namespace AlertaPulsera;

public partial class MainPage : ContentPage
{
    // ==========================
    // BLUETOOTH
    // ==========================

    private IAdapter? _adapter;
    private IDevice? _pulseraConectada;

    private readonly HashSet<Guid> _dispositivosVistos = new();

    private bool _buscando;
    private bool _paginaVisible;
    private bool _operacionBle;

    private int _mtuBle = 23;

    private static readonly Guid ServicioWifiUuid =
        Guid.Parse("6f8f1c70-3a90-4f5b-8a72-001000000001");

    private static readonly Guid SsidUuid =
        Guid.Parse("6f8f1c70-3a90-4f5b-8a72-001000000011");

    private static readonly Guid ClaveWifiUuid =
        Guid.Parse("6f8f1c70-3a90-4f5b-8a72-001000000012");

    // ==========================
    // PULSOS / FIREBASE
    // ==========================

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

        EnviarWifiButton.IsEnabled =
            !_operacionBle &&
            _pulseraConectada?.State == DeviceState.Connected;

        if (_cancelacionPulsos != null)
            return;

        var cancelacion = new CancellationTokenSource();

        _cancelacionPulsos = cancelacion;

        _ = EscucharPulsosAsync(cancelacion);
    }

    protected override async void OnDisappearing()
    {
        _paginaVisible = false;
        _buscando = false;

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

    // ==========================
    // BUSCAR BLUETOOTH
    // ==========================

    private async void OnBuscarBluetoothClicked(
        object sender,
        EventArgs e)
    {
        if (_buscando || _operacionBle)
            return;

        _buscando = true;

        BuscarBluetoothButton.IsEnabled = false;
        EnviarWifiButton.IsEnabled = false;

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

            _adapter.DeviceDiscovered -= OnDispositivoEncontrado;
            _adapter.DeviceDiscovered += OnDispositivoEncontrado;

            EstadoBluetoothLabel.Text =
                "Buscando dispositivos BLE durante 10 segundos...";

            await _adapter.StartScanningForDevicesAsync();

            if (_paginaVisible && !_operacionBle)
            {
                EstadoBluetoothLabel.Text =
                    $"Búsqueda terminada: " +
                    $"{_dispositivosVistos.Count} dispositivos encontrados.";
            }
        }
        catch (Exception ex)
        {
            EstadoBluetoothLabel.Text =
                "No se pudo buscar Bluetooth.";

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

            BuscarBluetoothButton.IsEnabled = !_operacionBle;

            EnviarWifiButton.IsEnabled =
                !_operacionBle &&
                _pulseraConectada?.State == DeviceState.Connected;
        }
    }

    private void OnDispositivoEncontrado(
        object? sender,
        DeviceEventArgs e)
    {
        var dispositivo = e.Device;

        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (!_paginaVisible || !_buscando || _operacionBle)
                return;

            if (!_dispositivosVistos.Add(dispositivo.Id))
                return;

            string nombre =
                string.IsNullOrWhiteSpace(dispositivo.Name)
                    ? "Dispositivo sin nombre"
                    : dispositivo.Name;

            var informacion = new Label
            {
                Text = $"{nombre}\n" +
                       $"ID: {dispositivo.Id}\n" +
                       $"Señal: {dispositivo.Rssi} dBm",
                FontSize = 14
            };

            var botonConectar = new Button
            {
                Text = "Conectar"
            };

            botonConectar.Clicked += async (_, _) =>
            {
                if (_operacionBle)
                    return;

                botonConectar.IsEnabled = false;

                try
                {
                    await ConectarPulseraAsync(dispositivo);
                }
                finally
                {
                    botonConectar.IsEnabled = true;
                }
            };

            var tarjeta = new VerticalStackLayout
            {
                Spacing = 5,
                Margin = new Thickness(0, 5)
            };

            tarjeta.Children.Add(informacion);
            tarjeta.Children.Add(botonConectar);

            DispositivosLayout.Children.Add(tarjeta);
        });
    }

    // ==========================
    // CONECTAR PULSERA
    // ==========================

    private async Task ConectarPulseraAsync(IDevice dispositivo)
    {
        if (_operacionBle)
            return;

        _operacionBle = true;

        BuscarBluetoothButton.IsEnabled = false;
        EnviarWifiButton.IsEnabled = false;

        try
        {
            _adapter ??= CrossBluetoothLE.Current.Adapter;

            _buscando = false;

            if (_adapter.IsScanning)
            {
                await _adapter.StopScanningForDevicesAsync();
            }

            EstadoBluetoothLabel.Text =
                "Conectando a la pulsera...";

            EstadoWifiLabel.Text =
                "Verificando servicio de configuración...";

            if (_pulseraConectada != null &&
                _pulseraConectada.Id != dispositivo.Id &&
                _pulseraConectada.State == DeviceState.Connected)
            {
                await _adapter.DisconnectDeviceAsync(
                    _pulseraConectada);
            }

            _pulseraConectada = null;

            if (dispositivo.State != DeviceState.Connected)
            {
                await _adapter.ConnectToDeviceAsync(dispositivo);
            }

            var servicio = await dispositivo.GetServiceAsync(
                ServicioWifiUuid);

            if (servicio == null)
            {
                throw new InvalidOperationException(
                    "Este dispositivo no tiene el servicio " +
                    "Bluetooth de tu pulsera.");
            }

            var ssid = await servicio.GetCharacteristicAsync(
                SsidUuid);

            var clave = await servicio.GetCharacteristicAsync(
                ClaveWifiUuid);

            if (ssid == null || clave == null)
            {
                throw new InvalidOperationException(
                    "No se encontraron las características " +
                    "de nombre y contraseña Wi-Fi.");
            }

            if (!ssid.CanWrite || !clave.CanWrite)
            {
                throw new InvalidOperationException(
                    "Las características Wi-Fi no permiten escritura. " +
                    "Revisa el programa de la ESP32.");
            }

            _mtuBle = 23;

            try
            {
                int mtu = await dispositivo.RequestMtuAsync(185);

                if (mtu >= 23)
                {
                    _mtuBle = mtu;
                }
            }
            catch
            {
                _mtuBle = 23;
            }

            _pulseraConectada = dispositivo;

            EstadoBluetoothLabel.Text =
                "Pulsera conectada por Bluetooth.";

            EstadoWifiLabel.Text =
                "Escribe el nombre y la contraseña del Wi-Fi.";

            EnviarWifiButton.IsEnabled = true;
        }
        catch (Exception ex)
        {
            _pulseraConectada = null;

            try
            {
                if (dispositivo.State == DeviceState.Connected)
                {
                    await _adapter!
                        .DisconnectDeviceAsync(dispositivo);
                }
            }
            catch
            {
                // Conservar el error original.
            }

            EstadoBluetoothLabel.Text =
                "No se pudo conectar la pulsera.";

            EstadoWifiLabel.Text =
                "Conecta la pulsera antes de enviar el Wi-Fi.";

            await DisplayAlert(
                "Bluetooth",
                ex.Message,
                "Aceptar");
        }
        finally
        {
            _operacionBle = false;

            BuscarBluetoothButton.IsEnabled = true;
        }
    }

    // ==========================
    // ENVIAR DATOS WI-FI
    // ==========================

    private async void OnEnviarWifiClicked(
        object sender,
        EventArgs e)
    {
        if (_operacionBle || _buscando)
            return;

        var dispositivo = _pulseraConectada;

        if (dispositivo == null ||
            dispositivo.State != DeviceState.Connected)
        {
            EnviarWifiButton.IsEnabled = false;

            EstadoWifiLabel.Text =
                "La pulsera no está conectada. " +
                "Búscala y vuelve a conectarte.";

            return;
        }

        // Los espacios pueden formar parte del nombre o contraseña.
        string ssid = WifiNombreEntry.Text ?? "";
        string clave = WifiClaveEntry.Text ?? "";

        byte[] bytesSsid = Encoding.UTF8.GetBytes(ssid);
        byte[] bytesClave = Encoding.UTF8.GetBytes(clave);

        if (bytesSsid.Length == 0 || bytesSsid.Length > 32)
        {
            await DisplayAlert(
                "Wi-Fi",
                "El nombre de la red debe ocupar entre 1 y 32 bytes.",
                "Aceptar");

            return;
        }

        int maximoEscritura = Math.Min(_mtuBle - 3, 512);

        if (bytesSsid.Length > maximoEscritura ||
            bytesClave.Length > maximoEscritura)
        {
            await DisplayAlert(
                "Bluetooth",
                $"La conexión permite {maximoEscritura} bytes " +
                "por escritura y uno de los datos supera ese tamaño. " +
                "No se enviará cortado. Hay que ajustar el MTU " +
                "o el protocolo de recepción de la ESP32.",
                "Aceptar");

            return;
        }

        _operacionBle = true;

        EnviarWifiButton.IsEnabled = false;
        BuscarBluetoothButton.IsEnabled = false;

        try
        {
            EstadoWifiLabel.Text =
                "Enviando nombre de la red...";

            var servicio = await dispositivo.GetServiceAsync(
                ServicioWifiUuid);

            if (servicio == null)
            {
                throw new InvalidOperationException(
                    "No se encontró el servicio Bluetooth.");
            }

            var caracteristicaSsid =
                await servicio.GetCharacteristicAsync(SsidUuid);

            var caracteristicaClave =
                await servicio.GetCharacteristicAsync(ClaveWifiUuid);

            if (caracteristicaSsid == null ||
                caracteristicaClave == null ||
                !caracteristicaSsid.CanWrite ||
                !caracteristicaClave.CanWrite)
            {
                throw new InvalidOperationException(
                    "No se puede escribir la configuración Wi-Fi " +
                    "en esta pulsera.");
            }

            await caracteristicaSsid.WriteAsync(bytesSsid);

            EstadoWifiLabel.Text =
                "Enviando contraseña...";

            await caracteristicaClave.WriteAsync(bytesClave);

            EstadoWifiLabel.Text =
                "Escrituras Bluetooth completadas. ";
        }
        catch (Exception ex)
        {
            EstadoWifiLabel.Text =
                "No se completó el envío. " +
                "Vuelve a enviar ambos datos.";

            await DisplayAlert(
                "Configuración Wi-Fi",
                ex.Message,
                "Aceptar");
        }
        finally
        {
            _operacionBle = false;

            BuscarBluetoothButton.IsEnabled = true;

            EnviarWifiButton.IsEnabled =
                _pulseraConectada?.State == DeviceState.Connected;
        }
    }

    // ==========================
    // REGISTRAR NOTIFICACIONES
    // ==========================

    private async void OnActivarClicked(
    object sender,
    EventArgs e)
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

            const string claveInstalacion =
                "pulsera_instalacion_id";

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

            EstadoLabel.Text =
                "Guardando registro de notificaciones...";

            var datos = new Dictionary<object, object>
        {
            { "token", token },
            { "plataforma", "android" },
            {
                "actualizadoUtc",
                DateTime.UtcNow.ToString("O")
            }
        };

            var ruta =
                $"usuarios/{usuario.Uid}/dispositivos/{instalacionId}";

            await CrossFirebaseFirestore.Current
                .GetDocument(ruta)
                .SetDataAsync(datos);

            EstadoLabel.Text =
                "Notificaciones activadas correctamente.";
        }
        catch (Exception)
        {
            EstadoLabel.Text =
                "No se pudieron activar las notificaciones.";

            await DisplayAlert(
                "Notificaciones",
                "No se pudo completar el registro. " +
                "Intenta de nuevo.",
                "Aceptar");
        }
        finally
        {
            if (sender is Button botonFinal)
                botonFinal.IsEnabled = true;
        }
    }

    // ==========================
    // ESCUCHA DE PULSOS
    // ==========================

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

                    // Token de autenticación, no el token FCM.
                    var resultadoToken =
                        await usuario.GetIdTokenResultAsync();

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
                    // No registrar la URL: contiene el token.
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