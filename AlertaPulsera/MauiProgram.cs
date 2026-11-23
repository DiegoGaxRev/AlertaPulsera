using Microsoft.Extensions.Logging;
using Microsoft.Maui.LifecycleEvents;

#if ANDROID
using Plugin.Firebase.Core.Platforms.Android;
#endif

namespace AlertaPulsera
{
    public static class MauiProgram
    {
        public static MauiApp CreateMauiApp()
        {
            var builder = MauiApp.CreateBuilder();

            builder
                .UseMauiApp<App>()
                .ConfigureFonts(fonts =>
                {
                    fonts.AddFont(
                        "OpenSans-Regular.ttf",
                        "OpenSansRegular");

                    fonts.AddFont(
                        "OpenSans-Semibold.ttf",
                        "OpenSansSemibold");
                })
               .ConfigureLifecycleEvents(events =>
               {
#if ANDROID
                   events.AddAndroid(android =>
                       android.OnCreate((activity, _) =>
                       {
                           CrossFirebase.Initialize(
                               activity,
                               () => Microsoft.Maui.ApplicationModel.Platform.CurrentActivity!
                           );
                       }));
#endif
               });

#if DEBUG
            builder.Logging.AddDebug();
#endif

            return builder.Build();
        }
    }
}
