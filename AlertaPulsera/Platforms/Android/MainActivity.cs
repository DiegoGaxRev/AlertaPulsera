using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Plugin.Firebase.CloudMessaging;

namespace AlertaPulsera
{
    [Activity(
        Theme = "@style/Maui.SplashTheme",
        MainLauncher = true,
        LaunchMode = LaunchMode.SingleTop,
        ConfigurationChanges =
            ConfigChanges.ScreenSize |
            ConfigChanges.Orientation |
            ConfigChanges.UiMode |
            ConfigChanges.ScreenLayout |
            ConfigChanges.SmallestScreenSize |
            ConfigChanges.Density)]
    public class MainActivity : MauiAppCompatActivity
    {
        protected override void OnCreate(Bundle? savedInstanceState)
        {
            base.OnCreate(savedInstanceState);

            CrearCanalNotificaciones();
            ProcesarIntent(Intent);
        }

        protected override void OnNewIntent(Intent? intent)
        {
            base.OnNewIntent(intent);

            ProcesarIntent(intent);
        }

        private static void ProcesarIntent(Intent? intent)
        {
            if (intent != null)
            {
                FirebaseCloudMessagingImplementation.OnNewIntent(intent);
            }
        }

        private void CrearCanalNotificaciones()
        {
            if (Build.VERSION.SdkInt < BuildVersionCodes.O)
                return;

            var canalId = $"{PackageName}.alertas";

            var canal = new NotificationChannel(
                canalId,
                "Alertas de la pulsera",
                NotificationImportance.High)
            {
                Description = "Avisos de posibles caídas de la pulsera."
            };

            var administrador =
                GetSystemService(NotificationService)
                as NotificationManager;

            administrador?.CreateNotificationChannel(canal);

            FirebaseCloudMessagingImplementation.ChannelId = canalId;
        }
    }
}