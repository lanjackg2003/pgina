﻿/*
	Copyright (c) 2011, pGina Team
	All rights reserved.

	Redistribution and use in source and binary forms, with or without
	modification, are permitted provided that the following conditions are met:
		* Redistributions of source code must retain the above copyright
		  notice, this list of conditions and the following disclaimer.
		* Redistributions in binary form must reproduce the above copyright
		  notice, this list of conditions and the following disclaimer in the
		  documentation and/or other materials provided with the distribution.
		* Neither the name of the pGina Team nor the names of its contributors 
		  may be used to endorse or promote products derived from this software without 
		  specific prior written permission.

	THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND
	ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
	WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
	DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE FOR ANY
	DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
	(INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
	LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
	ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
	(INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
	SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
*/
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Diagnostics;
using System.Security.Principal;
using System.DirectoryServices.AccountManagement;

using log4net;

using pGina.Shared.Interfaces;
using pGina.Shared.Types;

namespace pGina.Plugin.LocalMachine
{

    public class PluginImpl : IPluginAuthentication, IPluginAuthorization, IPluginAuthenticationGateway, IPluginConfiguration
    {
        public static Guid PluginUuid
        {
            get { return new Guid("{12FA152D-A2E3-4C8D-9535-5DCD49DFCB6D}"); }
        }

        private ILog m_logger = LogManager.GetLogger("LocalMachine");

        public PluginImpl()
        {
            using(Process me = Process.GetCurrentProcess())
            {
                m_logger.DebugFormat("Plugin initialized on {0} in PID: {1} Session: {2}", Environment.MachineName, me.Id, me.SessionId);
            }
        }
 
        public string Name
        {
            get { return "Local Machine"; }
        }

        public string Description
        {
            get { return "Manages local machine accounts for authenticated users, and authenticates against the local SAM"; }
        }

        public string Version
        {
            get { return "1.0.0"; }
        }

        public Guid Uuid
        {
            get { return PluginUuid; }
        }
        
        public BooleanResult AuthenticatedUserGateway(SessionProperties properties)
        {
            // Our job, if we've been elected to do gateway, is to ensure that an
            //  authenticated user:
            //
            //  1. Has a local account
            //  2. That account's password is set to the one they used to authenticate
            //  3. That account is a member of all groups listed, and not a member of any others
            //  
            // Is failure at #3 a total fail?
            bool failIfGroupSyncFails = Settings.Store.GroupCreateFailIsFAIL;

            try
            {
                UserInformation userInfo = properties.GetTrackedSingle<UserInformation>();
                m_logger.DebugFormat("AuthenticatedUserGateway({0}) for user: {1}", properties.Id.ToString(), userInfo.Username);
                LocalAccount.SyncUserInfoToLocalUser(userInfo);
            }
            catch (LocalAccount.GroupSyncException e)
            {
                if (failIfGroupSyncFails)
                    return new BooleanResult() { Success = false, Message = string.Format("Unable to sync users local group membership: {0}", e.RootException) };
            }
            catch(Exception e)
            {
                return new BooleanResult() { Success = false, Message = string.Format("Unexpected error while syncing user's info: {0}", e) };
            }

            return new BooleanResult() { Success = true };
        }

        public BooleanResult AuthenticateUser(SessionProperties properties)
        {
            try
            {                
                m_logger.DebugFormat("AuthenticateUser({0})", properties.Id.ToString());

                // Get user info
                UserInformation userInfo = properties.GetTrackedSingle<UserInformation>();

                m_logger.DebugFormat("Found username: {0}", userInfo.Username);

                using(PrincipalContext pc = new PrincipalContext(ContextType.Machine, Environment.MachineName))
                {
                    if(pc.ValidateCredentials(userInfo.Username, userInfo.Password))
                    {
                        m_logger.InfoFormat("Authenticated user: {0}", userInfo.Username);                        
                        userInfo.Domain = Environment.MachineName;                                             

                        // Now fill out the users group membership list so it matches their local memberships
                        LocalAccount.SyncLocalUserGroupsToUserInfo(userInfo);
                        userInfo.AddGroup(new GroupInformation() { Name = "pGinaTest" });

                        return new BooleanResult() { Success = true };
                    }
                }                                

                m_logger.ErrorFormat("Failed to authenticate user: {0}", userInfo.Username);
                return new BooleanResult() { Success = false, Message = string.Format("Local account validation failed.") };
            }
            catch (Exception e)
            {
                m_logger.ErrorFormat("AuthenticateUser exception: {0}", e);
                throw;  // Allow pGina service to catch and handle exception
            }
        }

        public BooleanResult AuthorizeUser(SessionProperties properties)
        {
            // Do we need to do authorization?
            if (DoesAuthzApply(properties))
            {
                bool limitToLocalAdmins = Settings.Store.AuthzLocalAdminsOnly;
                string[] limitToGroupList = Settings.Store.AuthzLocalGroups;
                bool restrictionsApply = limitToLocalAdmins || (limitToGroupList.Length > 0);
                PluginActivityInformation pluginInfo = properties.GetTrackedSingle<PluginActivityInformation>();

                if (!restrictionsApply)
                {
                    return new BooleanResult() { Success = true };
                }
                else if (!pluginInfo.LoadedAuthenticationGatewayPlugins.Contains(this))
                {
                    return new BooleanResult()
                    {
                        Success = false,
                        Message = string.Format("Plugin configured to authorize users based on group membership, but not in the gateway list to ensure membership is enforced, denying access")
                    };
                }

                // The user must have the local administrator group in his group list, and 
                //  we must be in the Gateway list of plugins (as we'll be the ones ensuring
                //  this group membership is enforced).
                if (limitToLocalAdmins)
                {
                    SecurityIdentifier adminSid = Abstractions.Windows.Security.GetWellknownSID(WellKnownSidType.BuiltinAdministratorsSid);
                    string adminName = Abstractions.Windows.Security.GetNameFromSID(adminSid);                    
                   
                    if(!ListedInGroup(adminName, adminSid, properties))
                    {
                        return new BooleanResult()
                        {
                            Success = false,
                            Message = string.Format("Users group list does not include the admin group ({0}), denying access", adminName)
                        };
                    }
                }

                // The user must have one of the groups listed (by name) in their group list
                // and we must be in the Gateway list of plugins (as we'll be the ones ensuring
                //  this group membership is enforced).
                if (limitToGroupList.Length > 0)
                {
                    foreach (string group in limitToGroupList)
                    {
                        if (!ListedInGroup(group, null, properties))
                        {
                            return new BooleanResult()
                            {
                                Success = false,
                                Message = string.Format("Users group list does not include the required group ({0}), denying access", group)
                            };
                        }
                    }
                }

                return new BooleanResult() { Success = true };
            }
            else
            {
                // We elect to not do any authorization, let the user pass for us
                return new BooleanResult() { Success = true };
            }
        }        

        public void Configure()
        {
            Configuration dialog = new Configuration();
            dialog.ShowDialog();
        }

        private bool ListedInGroup(string name, SecurityIdentifier sid, SessionProperties properties)
        {
            UserInformation userInfo = properties.GetTrackedSingle<UserInformation>();
            foreach (GroupInformation group in userInfo.Groups)
            {
                if (group.Name == name || (sid != null && group.SID == sid))
                    return true;
            }

            return false;
        }

        private bool DoesAuthzApply(SessionProperties properties)
        {
            // Do we authorize all users?
            bool authzAllUsers = Settings.Store.AuthzApplyToAllUsers;
            if (authzAllUsers) return true; 
            
            // Did we auth this user?
            PluginActivityInformation pluginInfo = properties.GetTrackedSingle<PluginActivityInformation>();                
            return pluginInfo.GetAuthenticatedPlugins().Contains(PluginUuid);           
        }

        
    }
}
