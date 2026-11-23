namespace AlertaPulsera;

public class PermisosBluetooth : Permissions.BasePlatformPermission
{
#if ANDROID
    public override (string androidPermission, bool isRuntime)[]
        RequiredPermissions
    {
        get
        {
            if (OperatingSystem.IsAndroidVersionAtLeast(31))
            {
                return new[]
                {
                    ("android.permission.BLUETOOTH_SCAN", true),
                    ("android.permission.BLUETOOTH_CONNECT", true)
                };
            }

            return new[]
            {
                ("android.permission.ACCESS_FINE_LOCATION", true)
            };
        }
    }
#endif
}