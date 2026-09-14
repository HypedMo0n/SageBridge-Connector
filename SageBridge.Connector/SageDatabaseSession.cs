using SimplySDK;

namespace SageBridge.Connector
{
    public interface ISageDatabaseSession
    {
        bool OpenDatabase(SageCompanyProfile profile, out string result);
        void CloseDatabase();
    }

    internal sealed class SimplySageDatabaseSession : ISageDatabaseSession
    {
        public bool OpenDatabase(SageCompanyProfile profile, out string result)
        {
            SDKInstanceManager.SDKResult sdkResult;
            var opened = SDKInstanceManager.Instance.OpenDatabase(
                profile.SageCompanyPath,
                profile.SageUsername,
                profile.SagePassword,
                profile.SageMultiUser ?? true,
                "SageBridge Connector",
                "SGBRDG",
                1,
                out sdkResult);
            result = sdkResult.ToString();
            return opened;
        }

        public void CloseDatabase()
        {
            SDKInstanceManager.Instance.CloseDatabase();
        }
    }
}
