// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.AccessControl;
using System.Security.Principal;
using System.ServiceProcess;
using System.Threading;
using Microsoft.VisualStudio.Services.Agent.Util;
using Microsoft.Win32;

namespace Microsoft.VisualStudio.Services.Agent.Listener.Configuration
{
    [ServiceLocator(Default = typeof(NativeWindowsServiceHelper))]
    public interface INativeWindowsServiceHelper : IAgentService
    {
        string GetUniqueBuildGroupName();

        bool LocalGroupExists(string groupName);

        void CreateLocalGroup(string groupName);

        void DeleteLocalGroup(string groupName);

        void AddMemberToLocalGroup(string accountName, string groupName);

        void GrantFullControlToGroup(string path, string groupName);

        void RemoveGroupFromFolderSecuritySetting(string folderPath, string groupName);

        bool IsUserHasLogonAsServicePrivilege(string domain, string userName);

        bool GrantUserLogonAsServicePrivilage(string domain, string userName);

        bool IsValidCredential(string domain, string userName, string logonPassword);

        NTAccount GetDefaultServiceAccount();

        NTAccount GetDefaultAdminServiceAccount();

        bool IsServiceExists(string serviceName);

        void InstallService(string serviceName, string serviceDisplayName, string logonAccount, string logonPassword, bool setServiceSidTypeAsUnrestricted);

        void UninstallService(string serviceName);

        void StartService(string serviceName);

        void StopService(string serviceName);

        void CreateVstsAgentRegistryKey();

        void DeleteVstsAgentRegistryKey();

        string GetSecurityId(string domainName, string userName);

        void SetAutoLogonPassword(string password);

        void ResetAutoLogonPassword();

        bool IsRunningInElevatedMode();

        void LoadUserProfile(string domain, string userName, string logonPassword, out IntPtr tokenHandle, out PROFILEINFO userProfile);

        void UnloadUserProfile(IntPtr tokenHandle, PROFILEINFO userProfile);

        bool IsValidAutoLogonCredential(string domain, string userName, string logonPassword);

        void GrantDirectoryPermissionForAccount(string accountName, IList<string> folders);

        void RevokeDirectoryPermissionForAccount(IList<string> folders);
    }

    public class NativeWindowsServiceHelper : AgentService, INativeWindowsServiceHelper
    {
        private const string AgentServiceLocalGroupPrefix = "VSTS_AgentService_G";
        private ITerminal _term;

        public override void Initialize(IHostContext hostContext)
        {
            ArgUtil.NotNull(hostContext, nameof(hostContext));
            base.Initialize(hostContext);
            _term = hostContext.GetService<ITerminal>();
        }

        public string GetUniqueBuildGroupName()
        {
            return AgentServiceLocalGroupPrefix + IOUtil.GetPathHash(HostContext.GetDirectory(WellKnownDirectory.Bin)).Substring(0, 5);
        }

        // TODO: Make sure to remove Old agent's group and registry changes made during auto upgrade to vsts-agent.
        public bool LocalGroupExists(string groupName)
        {
            Trace.Entering();
            bool exists = false;

            IntPtr bufptr;
            int returnCode = NetLocalGroupGetInfo(null,            // computer name
                                                  groupName,
                                                  1,               // group info with comment
                                                  out bufptr);     // Win32GroupAPI.LocalGroupInfo

            try
            {
                switch (returnCode)
                {
                    case ReturnCode.S_OK:
                        Trace.Info($"Local group '{groupName}' exist.");
                        exists = true;
                        break;

                    case ReturnCode.NERR_GroupNotFound:
                    case ReturnCode.ERROR_NO_SUCH_ALIAS:
                        exists = false;
                        break;

                    case ReturnCode.ERROR_ACCESS_DENIED:
                        // NOTE: None of the exception thrown here are userName facing. The caller logs this exception and prints a more understandable error
                        throw new UnauthorizedAccessException(StringUtil.Loc("AccessDenied"));

                    default:
                        throw new InvalidOperationException(StringUtil.Loc("OperationFailed", nameof(NetLocalGroupGetInfo), returnCode));
                }
            }
            finally
            {
                // we don't need to actually read the info to determine whether it exists
                int bufferFreeError = NetApiBufferFree(bufptr);
                if (bufferFreeError != 0)
                {
                    Trace.Error(StringUtil.Format("Buffer free error, could not free buffer allocated, error code: {0}", bufferFreeError));
                }
            }

            return exists;
        }

        public void CreateLocalGroup(string groupName)
        {
            Trace.Entering();
            LocalGroupInfo groupInfo = new LocalGroupInfo();
            groupInfo.Name = groupName;
            groupInfo.Comment = StringUtil.Format("Built-in group used by Team Foundation Server.");

            int returnCode = NetLocalGroupAdd(null,               // computer name
                                              1,                  // 1 means include comment
                                              ref groupInfo,
                                              0);                 // param error number

            // return on success
            if (returnCode == ReturnCode.S_OK)
            {
                Trace.Info($"Local Group '{groupName}' created");
                return;
            }

            // Error Cases
            switch (returnCode)
            {
                case ReturnCode.NERR_GroupExists:
                case ReturnCode.ERROR_ALIAS_EXISTS:
                    Trace.Info(StringUtil.Format("Group {0} already exists", groupName));
                    break;
                case ReturnCode.ERROR_ACCESS_DENIED:
                    throw new UnauthorizedAccessException(StringUtil.Loc("AccessDenied"));

                case ReturnCode.ERROR_INVALID_PARAMETER:
                    throw new ArgumentException(StringUtil.Loc("InvalidGroupName", groupName));

                default:
                    throw new InvalidOperationException(StringUtil.Loc("OperationFailed", nameof(NetLocalGroupAdd), returnCode));
            }
        }

        public void DeleteLocalGroup(string groupName)
        {
            Trace.Entering();
            int returnCode = NetLocalGroupDel(null,  // computer name
                                              groupName);

            // return on success
            if (returnCode == ReturnCode.S_OK)
            {
                Trace.Info($"Local Group '{groupName}' deleted");
                return;
            }

            // Error Cases
            switch (returnCode)
            {
                case ReturnCode.NERR_GroupNotFound:
                case ReturnCode.ERROR_NO_SUCH_ALIAS:
                    Trace.Info(StringUtil.Format("Group {0} not exists.", groupName));
                    break;

                case ReturnCode.ERROR_ACCESS_DENIED:
                    throw new UnauthorizedAccessException(StringUtil.Loc("AccessDenied"));

                default:
                    throw new InvalidOperationException(StringUtil.Loc("OperationFailed", nameof(NetLocalGroupDel), returnCode));
            }
        }

        public void AddMemberToLocalGroup(string accountName, string groupName)
        {
            Trace.Entering();
            LocalGroupMemberInfo memberInfo = new LocalGroupMemberInfo();
            memberInfo.FullName = accountName;

            int returnCode = NetLocalGroupAddMembers(null,              // computer name
                                                     groupName,
                                                     3,                 // group info with fullname (vs sid)
                                                     ref memberInfo,
                                                     1);                //total entries

            // return on success
            if (returnCode == ReturnCode.S_OK)
            {
                Trace.Info($"Account '{accountName}' is added to local group '{groupName}'.");
                return;
            }

            // Error Cases
            switch (returnCode)
            {
                case ReturnCode.ERROR_MEMBER_IN_ALIAS:
                    Trace.Info(StringUtil.Format("Account {0} is already member of group {1}", accountName, groupName));
                    break;
                case ReturnCode.NERR_GroupNotFound:
                case ReturnCode.ERROR_NO_SUCH_ALIAS:
                    throw new ArgumentException(StringUtil.Loc("GroupDoesNotExists", groupName));

                case ReturnCode.ERROR_NO_SUCH_MEMBER:
                    throw new ArgumentException(StringUtil.Loc("MemberDoesNotExists", accountName));

                case ReturnCode.ERROR_INVALID_MEMBER:
                    throw new ArgumentException(StringUtil.Loc("InvalidMember"));

                case ReturnCode.ERROR_ACCESS_DENIED:
                    throw new UnauthorizedAccessException(StringUtil.Loc("AccessDenied"));

                default:
                    throw new InvalidOperationException(StringUtil.Loc("OperationFailed", nameof(NetLocalGroupAddMembers), returnCode));
            }
        }

        public void GrantFullControlToGroup(string path, string groupName)
        {
            Trace.Entering();
            if (IsGroupHasFullControl(path, groupName))
            {
                Trace.Info($"Local group '{groupName}' already has full control to path '{path}'.");
                return;
            }

            DirectoryInfo dInfo = new DirectoryInfo(path);
            DirectorySecurity dSecurity = dInfo.GetAccessControl();

            if (!dSecurity.AreAccessRulesCanonical)
            {
                Trace.Warning("Acls are not canonical, this may cause failure");
            }

            dSecurity.AddAccessRule(
                new FileSystemAccessRule(
                    groupName,
                    FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                    PropagationFlags.None,
                    AccessControlType.Allow));
            dInfo.SetAccessControl(dSecurity);
        }

        private bool IsGroupHasFullControl(string path, string groupName)
        {
            DirectoryInfo dInfo = new DirectoryInfo(path);
            DirectorySecurity dSecurity = dInfo.GetAccessControl();

            var allAccessRuls = dSecurity.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>();

            SecurityIdentifier sid = (SecurityIdentifier)new NTAccount(groupName).Translate(typeof(SecurityIdentifier));

            if (allAccessRuls.Any(x => x.IdentityReference.Value == sid.ToString() &&
                                       x.AccessControlType == AccessControlType.Allow &&
                                       x.FileSystemRights.HasFlag(FileSystemRights.FullControl) &&
                                       x.InheritanceFlags == (InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit) &&
                                       x.PropagationFlags == PropagationFlags.None))
            {
                return true;
            }
            else
            {
                return false;
            }
        }

        public bool IsUserHasLogonAsServicePrivilege(string domain, string userName)
        {
            Trace.Entering();

            ArgUtil.NotNullOrEmpty(userName, nameof(userName));
            bool userHasPermission = false;

            using (LsaPolicy lsaPolicy = new LsaPolicy())
            {
                IntPtr rightsPtr;
                uint count;
                uint result = LsaEnumerateAccountRights(lsaPolicy.Handle, GetSidBinaryFromWindows(domain, userName), out rightsPtr, out count);
                try
                {
                    if (result == 0)
                    {
                        IntPtr incrementPtr = rightsPtr;
                        for (int i = 0; i < count; i++)
                        {
                            LSA_UNICODE_STRING nativeRightString = Marshal.PtrToStructure<LSA_UNICODE_STRING>(incrementPtr);
                            string rightString = Marshal.PtrToStringUni(nativeRightString.Buffer);
                            Trace.Verbose($"Account {userName} has '{rightString}' right.");
                            if (string.Equals(rightString, s_logonAsServiceName, StringComparison.OrdinalIgnoreCase))
                            {
                                userHasPermission = true;
                            }

                            incrementPtr += Marshal.SizeOf(nativeRightString);
                        }
                    }
                    else
                    {
                        Trace.Error($"Can't enumerate account rights, return code {result}.");
                    }
                }
                finally
                {
                    result = LsaFreeMemory(rightsPtr);
                    if (result != 0)
                    {
                        Trace.Error(StringUtil.Format("Failed to free memory from LsaEnumerateAccountRights. Return code : {0} ", result));
                    }
                }
            }

            return userHasPermission;
        }

        public bool GrantUserLogonAsServicePrivilage(string domain, string userName)
        {
            Trace.Entering();
            ArgUtil.NotNullOrEmpty(userName, nameof(userName));
            using (LsaPolicy lsaPolicy = new LsaPolicy())
            {
                // STATUS_SUCCESS == 0
                uint result = LsaAddAccountRights(lsaPolicy.Handle, GetSidBinaryFromWindows(domain, userName), LogonAsServiceRights, 1);
                if (result == 0)
                {
                    Trace.Info($"Successfully grant logon as service privilage to account '{userName}'");
                    return true;
                }
                else
                {
                    Trace.Info($"Fail to grant logon as service privilage to account '{userName}', error code {result}.");
                    return false;
                }
            }
        }

        public static bool IsWellKnownIdentity(String accountName)
        {
            NTAccount ntaccount = new NTAccount(accountName);
            SecurityIdentifier sid = (SecurityIdentifier)ntaccount.Translate(typeof(SecurityIdentifier));

            SecurityIdentifier networkServiceSid = new SecurityIdentifier(WellKnownSidType.NetworkServiceSid, null);
            SecurityIdentifier localServiceSid = new SecurityIdentifier(WellKnownSidType.LocalServiceSid, null);
            SecurityIdentifier localSystemSid = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);

            return sid.Equals(networkServiceSid) ||
                   sid.Equals(localServiceSid) ||
                   sid.Equals(localSystemSid);
        }

        public bool IsValidCredential(string domain, string userName, string logonPassword)
        {
            return IsValidCredentialInternal(domain, userName, logonPassword, LOGON32_LOGON_NETWORK);
        }

        public bool IsValidAutoLogonCredential(string domain, string userName, string logonPassword)
        {
            return IsValidCredentialInternal(domain, userName, logonPassword, LOGON32_LOGON_INTERACTIVE);
        }

        public NTAccount GetDefaultServiceAccount()
        {
            SecurityIdentifier sid = new SecurityIdentifier(WellKnownSidType.NetworkServiceSid, domainSid: null);
            NTAccount account = sid.Translate(typeof(NTAccount)) as NTAccount;

            if (account == null)
            {
                throw new InvalidOperationException(StringUtil.Loc("NetworkServiceNotFound"));
            }

            return account;
        }

        public NTAccount GetDefaultAdminServiceAccount()
        {
            SecurityIdentifier sid = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, domainSid: null);
            NTAccount account = sid.Translate(typeof(NTAccount)) as NTAccount;

            if (account == null)
            {
                throw new InvalidOperationException(StringUtil.Loc("LocalSystemAccountNotFound"));
            }

            return account;
        }

        public void RemoveGroupFromFolderSecuritySetting(string folderPath, string groupName)
        {
            DirectoryInfo dInfo = new DirectoryInfo(folderPath);
            if (dInfo.Exists)
            {
                DirectorySecurity dSecurity = dInfo.GetAccessControl();

                var allAccessRuls = dSecurity.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>();

                SecurityIdentifier sid = (SecurityIdentifier)new NTAccount(groupName).Translate(typeof(SecurityIdentifier));

                foreach (FileSystemAccessRule ace in allAccessRuls)
                {
                    if (String.Equals(sid.ToString(), ace.IdentityReference.Value, StringComparison.OrdinalIgnoreCase))
                    {
                        dSecurity.RemoveAccessRuleSpecific(ace);
                    }
                }
                dInfo.SetAccessControl(dSecurity);
            }
        }

        public bool IsServiceExists(string serviceName)
        {
            Trace.Entering();
            ServiceController service = ServiceController.GetServices().FirstOrDefault(x => x.ServiceName.Equals(serviceName, StringComparison.OrdinalIgnoreCase));
            return service != null;
        }
        
        public void InstallService(string serviceName, string serviceDisplayName, string logonAccount, string logonPassword, bool setServiceSidTypeAsUnrestricted)
        {
            Trace.Entering();

            string agentServiceExecutable = "\"" + Path.Combine(HostContext.GetDirectory(WellKnownDirectory.Bin), WindowsServiceControlManager.WindowsServiceControllerName) + "\"";
            IntPtr scmHndl = IntPtr.Zero;
            IntPtr svcHndl = IntPtr.Zero;
            IntPtr tmpBuf = IntPtr.Zero;
            IntPtr svcLock = IntPtr.Zero;

            try
            {
                //invoke the service with special argument, that tells it to register an event log trace source (need to run as an admin)
                using (var processInvoker = HostContext.CreateService<IProcessInvoker>())
                {
                    processInvoker.OutputDataReceived += delegate (object sender, ProcessDataReceivedEventArgs message)
                    {
                        _term.WriteLine(message.Data);
                    };
                    processInvoker.ErrorDataReceived += delegate (object sender, ProcessDataReceivedEventArgs message)
                    {
                        _term.WriteLine(message.Data);
                    };

                    processInvoker.ExecuteAsync(workingDirectory: string.Empty,
                                                fileName: agentServiceExecutable,
                                                arguments: "init",
                                                environment: null,
                                                requireExitCodeZero: true,
                                                cancellationToken: CancellationToken.None).GetAwaiter().GetResult();
                }

                Trace.Verbose(StringUtil.Format("Trying to open SCManager."));
                scmHndl = OpenSCManager(null, null, ServiceManagerRights.AllAccess);
                if (scmHndl.ToInt64() <= 0)
                {
                    throw new InvalidOperationException(StringUtil.Loc("FailedToOpenSCM"));
                }

                Trace.Verbose(StringUtil.Format("Opened SCManager. Trying to create service {0}", serviceName));
                svcHndl = CreateService(scmHndl,
                                        serviceName,
                                        serviceDisplayName,
                                        ServiceRights.AllAccess,
                                        SERVICE_WIN32_OWN_PROCESS,
                                        ServiceBootFlag.AutoStart,
                                        ServiceError.Normal,
                                        agentServiceExecutable,
                                        null,
                                        IntPtr.Zero,
                                        null,
                                        logonAccount,
                                        logonPassword);
                if (svcHndl.ToInt64() <= 0)
                {
                    throw new InvalidOperationException(StringUtil.Loc("OperationFailed", nameof(CreateService), GetLastError()));
                }

                _term.WriteLine(StringUtil.Loc("ServiceInstalled", serviceName));

                //set recovery option to restart on failure.
                ArrayList failureActions = new ArrayList();
                //first failure, we will restart the service right away.
                failureActions.Add(new FailureAction(RecoverAction.Restart, 0));
                //second failure, we will restart the service after 1 min.
                failureActions.Add(new FailureAction(RecoverAction.Restart, 60000));
                //subsequent failures, we will restart the service after 1 min
                failureActions.Add(new FailureAction(RecoverAction.Restart, 60000));

                // Lock the Service Database
                svcLock = LockServiceDatabase(scmHndl);
                if (svcLock.ToInt64() <= 0)
                {
                    throw new InvalidOperationException(StringUtil.Loc("FailedToLockServiceDB"));
                }

                int[] actions = new int[failureActions.Count * 2];
                int currInd = 0;
                foreach (FailureAction fa in failureActions)
                {
                    actions[currInd] = (int)fa.Type;
                    actions[++currInd] = fa.Delay;
                    currInd++;
                }

                // Need to pack 8 bytes per struct
                tmpBuf = Marshal.AllocHGlobal(failureActions.Count * 8);
                // Move array into marshallable pointer
                Marshal.Copy(actions, 0, tmpBuf, failureActions.Count * 2);

                // Change service error actions
                // Set the SERVICE_FAILURE_ACTIONS struct
                SERVICE_FAILURE_ACTIONS sfa = new SERVICE_FAILURE_ACTIONS();
                sfa.cActions = failureActions.Count;
                sfa.dwResetPeriod = SERVICE_NO_CHANGE;
                sfa.lpCommand = String.Empty;
                sfa.lpRebootMsg = String.Empty;
                sfa.lpsaActions = tmpBuf.ToInt64();

                // Call the ChangeServiceFailureActions() abstraction of ChangeServiceConfig2()
                bool falureActionsResult = ChangeServiceFailureActions(svcHndl, SERVICE_CONFIG_FAILURE_ACTIONS, ref sfa);
                //Check the return
                if (!falureActionsResult)
                {
                    int lastErrorCode = (int)GetLastError();
                    Exception win32exception = new Win32Exception(lastErrorCode);
                    if (lastErrorCode == ReturnCode.ERROR_ACCESS_DENIED)
                    {
                        throw new SecurityException(StringUtil.Loc("AccessDeniedSettingRecoveryOption"), win32exception);
                    }
                    else
                    {
                        throw win32exception;
                    }
                }
                else
                {
                    _term.WriteLine(StringUtil.Loc("ServiceRecoveryOptionSet", serviceName));
                }

                // Change service to delayed auto start
                SERVICE_DELAYED_AUTO_START_INFO sdasi = new SERVICE_DELAYED_AUTO_START_INFO();
                sdasi.fDelayedAutostart = true;

                // Call the ChangeServiceDelayedAutoStart() abstraction of ChangeServiceConfig2()
                bool delayedStartResult = ChangeServiceDelayedAutoStart(svcHndl, SERVICE_CONFIG_DELAYED_AUTO_START_INFO, ref sdasi);
                //Check the return
                if (!delayedStartResult)
                {
                    int lastErrorCode = (int)GetLastError();
                    Exception win32exception = new Win32Exception(lastErrorCode);
                    if (lastErrorCode == ReturnCode.ERROR_ACCESS_DENIED)
                    {
                        throw new SecurityException(StringUtil.Loc("AccessDeniedSettingDelayedStartOption"), win32exception);
                    }
                    else
                    {
                        throw win32exception;
                    }
                }
                else
                {
                    _term.WriteLine(StringUtil.Loc("ServiceDelayedStartOptionSet", serviceName));
                }

                if (setServiceSidTypeAsUnrestricted)
                {
                    this.setServiceSidTypeAsUnrestricted(svcHndl, serviceName);
                }

                _term.WriteLine(StringUtil.Loc("ServiceConfigured", serviceName));
            }
            finally
            {
                if (scmHndl != IntPtr.Zero)
                {
                    // Unlock the service database
                    if (svcLock != IntPtr.Zero)
                    {
                        UnlockServiceDatabase(svcLock);
                        svcLock = IntPtr.Zero;
                    }

                    // Close the service control manager handle
                    CloseServiceHandle(scmHndl);
                    scmHndl = IntPtr.Zero;
                }

                // Close the service handle
                if (svcHndl != IntPtr.Zero)
                {
                    CloseServiceHandle(svcHndl);
                    svcHndl = IntPtr.Zero;
                }

                // Free the memory
                if (tmpBuf != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(tmpBuf);
                    tmpBuf = IntPtr.Zero;
                }
            }
        }

        public void UninstallService(string serviceName)
        {
            Trace.Entering();
            Trace.Verbose(StringUtil.Format("Trying to open SCManager."));
            IntPtr scmHndl = OpenSCManager(null, null, ServiceManagerRights.Connect);

            if (scmHndl.ToInt64() <= 0)
            {
                throw new InvalidOperationException(StringUtil.Loc("FailedToOpenSCManager"));
            }

            try
            {
                Trace.Verbose(StringUtil.Format("Opened SCManager. query installed service {0}", serviceName));
                IntPtr serviceHndl = OpenService(scmHndl,
                                                 serviceName,
                                                 ServiceRights.StandardRightsRequired | ServiceRights.Stop | ServiceRights.QueryStatus);

                if (serviceHndl == IntPtr.Zero)
                {
                    int lastError = Marshal.GetLastWin32Error();
                    throw new Win32Exception(lastError);
                }

                try
                {
                    Trace.Info(StringUtil.Format("Trying to delete service {0}", serviceName));
                    int result = DeleteService(serviceHndl);
                    if (result == 0)
                    {
                        result = Marshal.GetLastWin32Error();
                        throw new Win32Exception(result, StringUtil.Loc("CouldNotRemoveService", serviceName));
                    }

                    Trace.Info("successfully removed the service");
                }
                finally
                {
                    CloseServiceHandle(serviceHndl);
                }
            }
            finally
            {
                CloseServiceHandle(scmHndl);
            }
        }

        public void StartService(string serviceName)
        {
            Trace.Entering();
            try
            {
                ServiceController service = ServiceController.GetServices().FirstOrDefault(x => x.ServiceName.Equals(serviceName, StringComparison.OrdinalIgnoreCase));
                if (service != null)
                {
                    service.Start();
                    _term.WriteLine(StringUtil.Loc("ServiceStartedSuccessfully", serviceName));
                }
                else
                {
                    throw new InvalidOperationException(StringUtil.Loc("CanNotFindService", serviceName));
                }
            }
            catch (Exception exception)
            {
                Trace.Error(exception);
                _term.WriteError(StringUtil.Loc("CanNotStartService"));

                // This is the last step in the configuration. Even if the start failed the status of the configuration should be error
                // If its configured through scripts its mandatory we indicate the failure where configuration failed to start the service
                throw;
            }
        }

        public void StopService(string serviceName)
        {
            Trace.Entering();
            try
            {
                ServiceController service = ServiceController.GetServices().FirstOrDefault(x => x.ServiceName.Equals(serviceName, StringComparison.OrdinalIgnoreCase));
                if (service != null)
                {
                    if (service.Status == ServiceControllerStatus.Running)
                    {
                        Trace.Info("Trying to stop the service");
                        service.Stop();

                        try
                        {
                            _term.WriteLine(StringUtil.Loc("WaitForServiceToStop"));
                            service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(35));
                        }
                        catch (System.ServiceProcess.TimeoutException)
                        {
                            throw new InvalidOperationException(StringUtil.Loc("CanNotStopService", serviceName));
                        }
                    }

                    Trace.Info("Successfully stopped the service");
                }
                else
                {
                    Trace.Info(StringUtil.Loc("CanNotFindService", serviceName));
                }
            }
            catch (Exception exception)
            {
                Trace.Error(exception);
                _term.WriteError(StringUtil.Loc("CanNotStopService", serviceName));

                // Log the exception but do not report it as error. We can try uninstalling the service and then report it as error if something goes wrong.
            }
        }

        public void CreateVstsAgentRegistryKey()
        {
            RegistryKey tfsKey = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\TeamFoundationServer\15.0", true);
            if (tfsKey == null)
            {
                //We could be on a machine that doesn't have TFS installed on it, create the key
                tfsKey = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Microsoft\TeamFoundationServer\15.0");
            }

            if (tfsKey == null)
            {
                throw new ArgumentNullException("Unable to create regiestry key: 'HKLM\\SOFTWARE\\Microsoft\\TeamFoundationServer\\15.0'");
            }

            try
            {
                using (RegistryKey vstsAgentsKey = tfsKey.CreateSubKey("VstsAgents"))
                {
                    String hash = IOUtil.GetPathHash(HostContext.GetDirectory(WellKnownDirectory.Bin));
                    using (RegistryKey agentKey = vstsAgentsKey.CreateSubKey(hash))
                    {
                        agentKey.SetValue("InstallPath", Path.Combine(HostContext.GetDirectory(WellKnownDirectory.Bin), "Agent.Listener.exe"));
                    }
                }
            }
            finally
            {
                tfsKey.Dispose();
            }
        }

        public void DeleteVstsAgentRegistryKey()
        {
            RegistryKey tfsKey = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\TeamFoundationServer\15.0", true);
            if (tfsKey != null)
            {
                try
                {
                    RegistryKey vstsAgentsKey = tfsKey.OpenSubKey("VstsAgents", true);
                    if (vstsAgentsKey != null)
                    {
                        try
                        {
                            String hash = IOUtil.GetPathHash(HostContext.GetDirectory(WellKnownDirectory.Bin));
                            vstsAgentsKey.DeleteSubKeyTree(hash);
                        }
                        finally
                        {
                            vstsAgentsKey.Dispose();
                        }
                    }
                }
                finally
                {
                    tfsKey.Dispose();
                }
            }
        }

        public string GetSecurityId(string domainName, string userName)
        {
            var account = new NTAccount(domainName, userName);
            var sid = account.Translate(typeof(SecurityIdentifier));
            return sid != null ? sid.ToString() : null;
        }

        public void SetAutoLogonPassword(string password)
        {
            using (LsaPolicy lsaPolicy = new LsaPolicy(LSA_AccessPolicy.POLICY_CREATE_SECRET))
            {
                lsaPolicy.SetSecretData(LsaPolicy.DefaultPassword, password);
            }
        }

        public void ResetAutoLogonPassword()
        {
            using (LsaPolicy lsaPolicy = new LsaPolicy(LSA_AccessPolicy.POLICY_CREATE_SECRET))
            {
                lsaPolicy.SetSecretData(LsaPolicy.DefaultPassword, null);
            }
        }

        public bool IsRunningInElevatedMode()
        {
            return new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
        }

        public void LoadUserProfile(string domain, string userName, string logonPassword, out IntPtr tokenHandle, out PROFILEINFO userProfile)
        {
            Trace.Entering();
            tokenHandle = IntPtr.Zero;

            ArgUtil.NotNullOrEmpty(userName, nameof(userName));
            if (LogonUser(userName, domain, logonPassword, LOGON32_LOGON_INTERACTIVE, LOGON32_PROVIDER_DEFAULT, out tokenHandle) == 0)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            userProfile = new PROFILEINFO();
            userProfile.dwSize = Marshal.SizeOf(typeof(PROFILEINFO));
            userProfile.lpUserName = userName;
            if (!LoadUserProfile(tokenHandle, ref userProfile))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            Trace.Info($"Successfully loaded the profile for {domain}\\{userName}.");
        }

        public void UnloadUserProfile(IntPtr tokenHandle, PROFILEINFO userProfile)
        {
            Trace.Entering();

            if (tokenHandle == IntPtr.Zero)
            {
                Trace.Verbose("The handle to unload user profile is not set. Returning.");
            }

            if (!UnloadUserProfile(tokenHandle, userProfile.hProfile))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            Trace.Info($"Successfully unloaded the profile for {userProfile.lpUserName}.");
        }

        public void GrantDirectoryPermissionForAccount(string accountName, IList<string> folders)
        {
            ArgUtil.NotNull(folders, nameof(folders));
            Trace.Entering();
            string groupName = GetUniqueBuildGroupName();
            Trace.Info(StringUtil.Format("Calculated unique group name {0}", groupName));

            if (!LocalGroupExists(groupName))
            {
                Trace.Info(StringUtil.Format("Trying to create group {0}", groupName));
                CreateLocalGroup(groupName);
            }

            Trace.Info(StringUtil.Format("Trying to add userName {0} to the group {1}", accountName, groupName));
            AddMemberToLocalGroup(accountName, groupName);

            // grant permssion for folders
            foreach(var folder in folders)
            {
                if (Directory.Exists(folder))
                {
                    Trace.Info(StringUtil.Format("Set full access control to group for the folder {0}", folder));
                    GrantFullControlToGroup(folder, groupName);
                }
            }
        }

        public void RevokeDirectoryPermissionForAccount(IList<string> folders)
        {
            ArgUtil.NotNull(folders, nameof(folders));
            Trace.Entering();
            string groupName = GetUniqueBuildGroupName();
            Trace.Info(StringUtil.Format("Calculated unique group name {0}", groupName));

            // remove the group from folders
            foreach(var folder in folders)
            {
                if (Directory.Exists(folder))
                {
                    Trace.Info(StringUtil.Format($"Remove the group {groupName} for the folder {folder}."));
                    try
                    {
                        RemoveGroupFromFolderSecuritySetting(folder, groupName);
                    }
                    catch(Exception ex)
                    {
                        Trace.Error(ex);
                    }
                }
            }

            //delete group
            Trace.Info(StringUtil.Format($"Delete the group {groupName}."));
            DeleteLocalGroup(groupName);
        }

        private bool IsValidCredentialInternal(string domain, string userName, string logonPassword, UInt32 logonType)
        {
            Trace.Entering();
            IntPtr tokenHandle = IntPtr.Zero;

            ArgUtil.NotNullOrEmpty(userName, nameof(userName));

            Trace.Info($"Verify credential for account {userName}.");
            int result = LogonUser(userName, domain, logonPassword, logonType, LOGON32_PROVIDER_DEFAULT, out tokenHandle);

            if (tokenHandle.ToInt32() != 0)
            {
                if (!CloseHandle(tokenHandle))
                {
                    Trace.Error("Failed during CloseHandle on token from LogonUser");
                }
            }

            if (result != 0)
            {
                Trace.Info($"Credential for account '{userName}' is valid.");
                return true;
            }
            else
            {
                Trace.Info($"Credential for account '{userName}' is invalid.");
                return false;
            }
        }

        private byte[] GetSidBinaryFromWindows(string domain, string user)
        {
            try
            {
                SecurityIdentifier sid = (SecurityIdentifier)new NTAccount(StringUtil.Format("{0}\\{1}", domain, user).TrimStart('\\')).Translate(typeof(SecurityIdentifier));
                byte[] binaryForm = new byte[sid.BinaryLength];
                sid.GetBinaryForm(binaryForm, 0);
                return binaryForm;
            }
            catch (Exception exception)
            {
                Trace.Error(exception);
                return null;
            }
        }

        // Helper class not to repeat whenever we deal with LSA* api
        internal class LsaPolicy : IDisposable
        {
            public IntPtr Handle { get; set; }

            public LsaPolicy()
                : this(LSA_AccessPolicy.POLICY_ALL_ACCESS)
            {
            }

            public LsaPolicy(LSA_AccessPolicy access)
            {
                LSA_UNICODE_STRING system = new LSA_UNICODE_STRING();
                LSA_OBJECT_ATTRIBUTES attrib = new LSA_OBJECT_ATTRIBUTES()
                {
                    Length = 0,
                    RootDirectory = IntPtr.Zero,
                    Attributes = 0,
                    SecurityDescriptor = IntPtr.Zero,
                    SecurityQualityOfService = IntPtr.Zero,
                };

                IntPtr handle = IntPtr.Zero;
                uint hr = LsaOpenPolicy(ref system, ref attrib, (uint)access, out handle);
                if (hr != 0 || handle == IntPtr.Zero)
                {
                    throw new InvalidOperationException(StringUtil.Loc("OperationFailed", nameof(LsaOpenPolicy), hr));
                }

                Handle = handle;
            }

            public void SetSecretData(string key, string value)
            {
                LSA_UNICODE_STRING secretData = new LSA_UNICODE_STRING();
                LSA_UNICODE_STRING secretName = new LSA_UNICODE_STRING();

                secretName.Buffer = Marshal.StringToHGlobalUni(key);

                var charSize = sizeof(char);

                secretName.Length = (UInt16)(key.Length * charSize);
                secretName.MaximumLength = (UInt16)((key.Length + 1) * charSize);

                if (value != null && value.Length > 0)
                {
                    // Create data and key
                    secretData.Buffer = Marshal.StringToHGlobalUni(value);
                    secretData.Length = (UInt16)(value.Length * charSize);
                    secretData.MaximumLength = (UInt16)((value.Length + 1) * charSize);
                }
                else
                {
                    // Delete data and key
                    secretData.Buffer = IntPtr.Zero;
                    secretData.Length = 0;
                    secretData.MaximumLength = 0;
                }

                uint result = LsaStorePrivateData(Handle, ref secretName, ref secretData);
                uint winErrorCode = LsaNtStatusToWinError(result);
                if (winErrorCode != 0)
                {
                    throw new InvalidOperationException(StringUtil.Loc("OperationFailed", nameof(LsaNtStatusToWinError), winErrorCode));
                }
            }

            void IDisposable.Dispose()
            {
                // We will ignore LsaClose error
                LsaClose(Handle);
                GC.SuppressFinalize(this);
            }

            internal static string DefaultPassword = "DefaultPassword";
        }

        internal enum LSA_AccessPolicy : long
        {
            POLICY_VIEW_LOCAL_INFORMATION = 0x00000001L,
            POLICY_VIEW_AUDIT_INFORMATION = 0x00000002L,
            POLICY_GET_PRIVATE_INFORMATION = 0x00000004L,
            POLICY_TRUST_ADMIN = 0x00000008L,
            POLICY_CREATE_ACCOUNT = 0x00000010L,
            POLICY_CREATE_SECRET = 0x00000020L,
            POLICY_CREATE_PRIVILEGE = 0x00000040L,
            POLICY_SET_DEFAULT_QUOTA_LIMITS = 0x00000080L,
            POLICY_SET_AUDIT_REQUIREMENTS = 0x00000100L,
            POLICY_AUDIT_LOG_ADMIN = 0x00000200L,
            POLICY_SERVER_ADMIN = 0x00000400L,
            POLICY_LOOKUP_NAMES = 0x00000800L,
            POLICY_NOTIFICATION = 0x00001000L,
            POLICY_ALL_ACCESS = 0x00001FFFL
        }

        [DllImport("advapi32.dll", SetLastError = true, PreserveSig = true)]
        public static extern uint LsaStorePrivateData(
            IntPtr policyHandle,
            ref LSA_UNICODE_STRING KeyName,
            ref LSA_UNICODE_STRING PrivateData
        );

        [DllImport("advapi32.dll", SetLastError = true, PreserveSig = true)]
        public static extern uint LsaNtStatusToWinError(
            uint status
        );

        private static UInt32 LOGON32_LOGON_INTERACTIVE = 2;
        private const UInt32 LOGON32_LOGON_NETWORK = 3;

        // Declaration of external pinvoke functions
        private static readonly string s_logonAsServiceName = "SeServiceLogonRight";

        private const UInt32 LOGON32_PROVIDER_DEFAULT = 0;

        private const int SERVICE_SID_TYPE_UNRESTRICTED = 0x00000001;
        private const int SERVICE_WIN32_OWN_PROCESS = 0x00000010;
        private const int SERVICE_NO_CHANGE = -1;
        private const int SERVICE_CONFIG_FAILURE_ACTIONS = 0x2;
        private const int SERVICE_CONFIG_DELAYED_AUTO_START_INFO = 0x3;
        private const int SERVICE_CONFIG_SERVICE_SID_INFO = 0x5;

        // TODO Fix this. This is not yet available in coreclr (newer version?)
        private const int UnicodeCharSize = 2;

        private static LSA_UNICODE_STRING[] LogonAsServiceRights
        {
            get
            {
                return new[]
                           {
                               new LSA_UNICODE_STRING()
                                   {
                                       Buffer = Marshal.StringToHGlobalUni(s_logonAsServiceName),
                                       Length = (UInt16)(s_logonAsServiceName.Length * UnicodeCharSize),
                                       MaximumLength = (UInt16) ((s_logonAsServiceName.Length + 1) * UnicodeCharSize)
                                   }
                           };
            }
        }

        public struct ReturnCode
        {
            public const int S_OK = 0;
            public const int ERROR_ACCESS_DENIED = 5;
            public const int ERROR_INVALID_PARAMETER = 87;
            public const int ERROR_MEMBER_NOT_IN_ALIAS = 1377; // member not in a group
            public const int ERROR_MEMBER_IN_ALIAS = 1378; // member already exists
            public const int ERROR_ALIAS_EXISTS = 1379;  // group already exists
            public const int ERROR_NO_SUCH_ALIAS = 1376;
            public const int ERROR_NO_SUCH_MEMBER = 1387;
            public const int ERROR_INVALID_MEMBER = 1388;
            public const int NERR_GroupNotFound = 2220;
            public const int NERR_GroupExists = 2223;
            public const int NERR_UserInGroup = 2236;
            public const uint STATUS_ACCESS_DENIED = 0XC0000022; //NTSTATUS error code: Access Denied
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct LocalGroupInfo
        {
            [MarshalAs(UnmanagedType.LPWStr)]
            public string Name;
            [MarshalAs(UnmanagedType.LPWStr)]
            public string Comment;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct LSA_UNICODE_STRING
        {
            public UInt16 Length;
            public UInt16 MaximumLength;

            // We need to use an IntPtr because if we wrap the Buffer with a SafeHandle-derived class, we get a failure during LsaAddAccountRights
            public IntPtr Buffer;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct LocalGroupMemberInfo
        {
            [MarshalAs(UnmanagedType.LPWStr)]
            public string FullName;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct LSA_OBJECT_ATTRIBUTES
        {
            public UInt32 Length;
            public IntPtr RootDirectory;
            public LSA_UNICODE_STRING ObjectName;
            public UInt32 Attributes;
            public IntPtr SecurityDescriptor;
            public IntPtr SecurityQualityOfService;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct SERVICE_FAILURE_ACTIONS
        {
            public int dwResetPeriod;
            public string lpRebootMsg;
            public string lpCommand;
            public int cActions;
            public long lpsaActions;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct SERVICE_DELAYED_AUTO_START_INFO
        {
            public bool fDelayedAutostart;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct SERVICE_SID_INFO
        {
            public int dwServiceSidType;
        }

        // Class to represent a failure action which consists of a recovery
        // action type and an action delay
        private class FailureAction
        {
            // Property to set recover action type
            public RecoverAction Type { get; set; }
            // Property to set recover action delay
            public int Delay { get; set; }

            // Constructor
            public FailureAction(RecoverAction actionType, int actionDelay)
            {
                Type = actionType;
                Delay = actionDelay;
            }
        }

        [Flags]
        public enum ServiceManagerRights
        {
            Connect = 0x0001,
            CreateService = 0x0002,
            EnumerateService = 0x0004,
            Lock = 0x0008,
            QueryLockStatus = 0x0010,
            ModifyBootConfig = 0x0020,
            StandardRightsRequired = 0xF0000,
            AllAccess =
                (StandardRightsRequired | Connect | CreateService | EnumerateService | Lock | QueryLockStatus
                 | ModifyBootConfig)
        }

        [Flags]
        public enum ServiceRights
        {
            QueryConfig = 0x1,
            ChangeConfig = 0x2,
            QueryStatus = 0x4,
            EnumerateDependants = 0x8,
            Start = 0x10,
            Stop = 0x20,
            PauseContinue = 0x40,
            Interrogate = 0x80,
            UserDefinedControl = 0x100,
            Delete = 0x00010000,
            StandardRightsRequired = 0xF0000,
            AllAccess =
                (StandardRightsRequired | QueryConfig | ChangeConfig | QueryStatus | EnumerateDependants | Start | Stop
                 | PauseContinue | Interrogate | UserDefinedControl)
        }

        public enum ServiceError
        {
            Ignore = 0x00000000,
            Normal = 0x00000001,
            Severe = 0x00000002,
            Critical = 0x00000003
        }

        public enum ServiceBootFlag
        {
            Start = 0x00000000,
            SystemStart = 0x00000001,
            AutoStart = 0x00000002,
            DemandStart = 0x00000003,
            Disabled = 0x00000004
        }

        // Enum for recovery actions (correspond to the Win32 equivalents )
        private enum RecoverAction
        {
            None = 0,
            Restart = 1,
            Reboot = 2,
            RunCommand = 3
        }

        [DllImport("Netapi32.dll")]
        private extern static int NetLocalGroupGetInfo(string servername,
                                                 string groupname,
                                                 int level,
                                                 out IntPtr bufptr);

        [DllImport("Netapi32.dll")]
        private extern static int NetApiBufferFree(IntPtr Buffer);


        [DllImport("Netapi32.dll")]
        private extern static int NetLocalGroupAdd([MarshalAs(UnmanagedType.LPWStr)] string servername,
                                                   int level,
                                                   ref LocalGroupInfo buf,
                                                   int parm_err);

        [DllImport("Netapi32.dll")]
        private extern static int NetLocalGroupAddMembers([MarshalAs(UnmanagedType.LPWStr)] string serverName,
                                                          [MarshalAs(UnmanagedType.LPWStr)] string groupName,
                                                          int level,
                                                          ref LocalGroupMemberInfo buf,
                                                          int totalEntries);

        [DllImport("Netapi32.dll")]
        public extern static int NetLocalGroupDel([MarshalAs(UnmanagedType.LPWStr)] string servername, [MarshalAs(UnmanagedType.LPWStr)] string groupname);

        [DllImport("advapi32.dll")]
        private static extern Int32 LsaClose(IntPtr ObjectHandle);

        [DllImport("advapi32.dll", SetLastError = true, PreserveSig = true)]
        private static extern uint LsaOpenPolicy(
            ref LSA_UNICODE_STRING SystemName,
            ref LSA_OBJECT_ATTRIBUTES ObjectAttributes,
            uint DesiredAccess,
            out IntPtr PolicyHandle);

        [DllImport("advapi32.dll", SetLastError = true, PreserveSig = true)]
        private static extern uint LsaAddAccountRights(
           IntPtr PolicyHandle,
           byte[] AccountSid,
           LSA_UNICODE_STRING[] UserRights,
           uint CountOfRights);

        [DllImport("advapi32.dll", SetLastError = true, PreserveSig = true)]
        public static extern uint LsaEnumerateAccountRights(
          IntPtr PolicyHandle,
          byte[] AccountSid,
          out IntPtr UserRights,
          out uint CountOfRights);

        [DllImport("advapi32.dll", SetLastError = true, PreserveSig = true)]
        public static extern uint LsaFreeMemory(IntPtr pBuffer);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern int LogonUser(string userName, string domain, string password, uint logonType, uint logonProvider, out IntPtr tokenHandle);

        [DllImport("userenv.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern Boolean LoadUserProfile(IntPtr hToken, ref PROFILEINFO lpProfileInfo);

        [DllImport("userenv.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern Boolean UnloadUserProfile(IntPtr hToken, IntPtr hProfile);

        [DllImport("kernel32", SetLastError = true)]
        public static extern bool CloseHandle(IntPtr handle);

        [DllImport("advapi32.dll", EntryPoint = "CreateServiceA")]
        private static extern IntPtr CreateService(
            IntPtr hSCManager,
            string lpServiceName,
            string lpDisplayName,
            ServiceRights dwDesiredAccess,
            int dwServiceType,
            ServiceBootFlag dwStartType,
            ServiceError dwErrorControl,
            string lpBinaryPathName,
            string lpLoadOrderGroup,
            IntPtr lpdwTagId,
            string lpDependencies,
            string lp,
            string lpPassword);

        [DllImport("advapi32.dll")]
        public static extern IntPtr OpenSCManager(string lpMachineName, string lpDatabaseName, ServiceManagerRights dwDesiredAccess);

        [DllImport("advapi32.dll", SetLastError = true)]
        public static extern IntPtr OpenService(IntPtr hSCManager, string lpServiceName, ServiceRights dwDesiredAccess);

        [DllImport("advapi32.dll", SetLastError = true)]
        public static extern int DeleteService(IntPtr hService);

        [DllImport("advapi32.dll")]
        public static extern int CloseServiceHandle(IntPtr hSCObject);

        [DllImport("advapi32.dll")]
        public static extern IntPtr LockServiceDatabase(IntPtr hSCManager);

        [DllImport("advapi32.dll")]
        public static extern bool UnlockServiceDatabase(IntPtr hSCManager);

        [DllImport("advapi32.dll", EntryPoint = "ChangeServiceConfig2")]
        public static extern bool ChangeServiceFailureActions(IntPtr hService, int dwInfoLevel, ref SERVICE_FAILURE_ACTIONS lpInfo);

        [DllImport("advapi32.dll", EntryPoint = "ChangeServiceConfig2")]
        public static extern bool ChangeServiceSidType(IntPtr hService, int dwInfoLevel, ref SERVICE_SID_INFO lpInfo);

        [DllImport("advapi32.dll", EntryPoint = "ChangeServiceConfig2")]
        public static extern bool ChangeServiceDelayedAutoStart(IntPtr hService, int dwInfoLevel, ref SERVICE_DELAYED_AUTO_START_INFO lpInfo);

        [DllImport("kernel32.dll")]
        static extern uint GetLastError();
        /// <summary>
        /// Sets service sid type as SERVICE_SID_TYPE_UNRESTRICTED - to make service more configurable for admins from the point of permissions 
        /// </summary>
        /// <param name="svcHndl">Service handler</param>
        /// <param name="serviceName">Service name</param>
        private void setServiceSidTypeAsUnrestricted(IntPtr svcHndl, string serviceName)
        {
            // Change service SID type to unrestricted
            SERVICE_SID_INFO ssi = new SERVICE_SID_INFO();
            ssi.dwServiceSidType = SERVICE_SID_TYPE_UNRESTRICTED;

            // Call the ChangeServiceDelayedAutoStart() abstraction of ChangeServiceConfig2()
            bool serviceSidTypeResult = ChangeServiceSidType(svcHndl, SERVICE_CONFIG_SERVICE_SID_INFO, ref ssi);
            //Check the return
            if (!serviceSidTypeResult)
            {
                int lastErrorCode = (int)GetLastError();
                Exception win32exception = new Win32Exception(lastErrorCode);
                if (lastErrorCode == ReturnCode.ERROR_ACCESS_DENIED)
                {
                    throw new SecurityException(StringUtil.Loc("AccessDeniedSettingSidType"), win32exception);
                }
                else
                {
                    throw win32exception;
                }
            }
            else
            {
                _term.WriteLine(StringUtil.Loc("ServiceSidTypeSet", serviceName));
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PROFILEINFO
    {
        public int dwSize;
        public int dwFlags;
        [MarshalAs(UnmanagedType.LPTStr)]
        public String lpUserName;
        [MarshalAs(UnmanagedType.LPTStr)]
        public String lpProfilePath;
        [MarshalAs(UnmanagedType.LPTStr)]
        public String lpDefaultPath;
        [MarshalAs(UnmanagedType.LPTStr)]
        public String lpServerName;
        [MarshalAs(UnmanagedType.LPTStr)]
        public String lpPolicyPath;
        public IntPtr hProfile;
    }
}
