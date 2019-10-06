/////////////////////////////////////////////////////////////////////
// Copyright (c) Autodesk, Inc. All rights reserved
// Written by Forge Partner Development
//
// Permission to use, copy, modify, and distribute this software in
// object code form for any purpose and without fee is hereby granted,
// provided that the above copyright notice appears in all copies and
// that both that copyright notice and the limited warranty and
// restricted rights notice below appear in all supporting
// documentation.
//
// AUTODESK PROVIDES THIS PROGRAM "AS IS" AND WITH ALL FAULTS.
// AUTODESK SPECIFICALLY DISCLAIMS ANY IMPLIED WARRANTY OF
// MERCHANTABILITY OR FITNESS FOR A PARTICULAR USE.  AUTODESK, INC.
// DOES NOT WARRANT THAT THE OPERATION OF THE PROGRAM WILL BE
// UNINTERRUPTED OR ERROR FREE.
/////////////////////////////////////////////////////////////////////

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Autodesk.Forge;
using Autodesk.Forge.Model;
using Newtonsoft.Json.Linq;
using System.Net;
using Hangfire;
using Hangfire.States;
using Hangfire.Server;
using Hangfire.Console;
using System.Linq;
using Microsoft.AspNetCore.SignalR;

namespace forgeSample.Controllers
{
    public class DataManagementCrawlerController : ControllerBase
    {
        private IHubContext<ModelDerivativeHub> _hubContext;
        public DataManagementCrawlerController(IHubContext<ModelDerivativeHub> hubContext) { _hubContext = hubContext; }

        /// <summary>
        /// GET TreeNode passing the ID
        /// </summary>
        [HttpGet]
        [Route("api/forge/datamanagement/{hubid}/index")]
        public async Task<IActionResult> GetTreeNodeAsync(string hubId)
        {
            Credentials credentials = await Credentials.FromSessionAsync(base.Request.Cookies, Response.Cookies);
            if (credentials == null) { return Unauthorized(); }

            BackgroundJobClient indexQueue = new BackgroundJobClient();
            IState state = new EnqueuedState("index");
            indexQueue.Create(() => IndexHubAsync(credentials.UserId, hubId, null), state);

            return Ok();
        }

        public async Task IndexHubAsync(string userId, string hubId, PerformContext context)
        {
            Credentials credentials = await Credentials.FromDatabaseAsync(userId);

            // ToDo: check if hub is already indexed

            await IndexProjectsAsync(credentials, hubId, context);
            await ModelDerivativeHub.NotifyHubComplete(_hubContext, hubId);
        }

        private async Task IndexProjectsAsync(Credentials credentials, string hubId, PerformContext context)
        {
            // the API SDK
            ProjectsApi projectsApi = new ProjectsApi();
            projectsApi.Configuration.AccessToken = credentials.TokenInternal;

            var projects = await projectsApi.GetHubProjectsAsync(hubId);
            foreach (KeyValuePair<string, dynamic> projectInfo in new DynamicDictionaryItems(projects.data))
            {
                var folders = await projectsApi.GetProjectTopFoldersAsync(hubId, projectInfo.Value.id);
                foreach (KeyValuePair<string, dynamic> folder in new DynamicDictionaryItems(folders.data))
                {
                    if (folder.Value.attributes.displayName != "Project Files") continue;
                    await GetFolderContentsAsync(credentials, hubId, folder.Value.links.self.href, context);

                    // 
                    string[] hrefParams = folder.Value.links.self.href.Split('/');
                    string folderUrn = hrefParams[hrefParams.Length - 1];

                    // start listening for this folder (Project Files)
                    DMWebhook webhooksApi = new DMWebhook(credentials.TokenInternal, Config.WebhookUrl);
                    await webhooksApi.DeleteHook(Event.VersionAdded, folderUrn); // remove old (needed when doing local test with ngrok or to create a new one)
                    await webhooksApi.CreateHook(Event.VersionAdded, projectInfo.Value.id, folderUrn);
                }
            }

        }

        private async Task GetFolderContentsAsync(Credentials credentials, string hubId, string folderHref, PerformContext context)
        {
            // the API SDK
            FoldersApi folderApi = new FoldersApi();
            folderApi.Configuration.AccessToken = credentials.TokenInternal;

            // extract the projectId & folderId from the href
            string[] idParams = folderHref.Split('/');
            string folderUrn = idParams[idParams.Length - 1];
            string projectId = idParams[idParams.Length - 3];

            var folderContents = await folderApi.GetFolderContentsAsync(projectId, folderUrn);
            var folderData = new DynamicDictionaryItems(folderContents.data);

            // let's start iterating the FOLDER DATA
            foreach (KeyValuePair<string, dynamic> folderContentItem in folderData)
            {
                if ((string)folderContentItem.Value.type == "folders")
                {
                    // get subfolder...
                    await GetFolderContentsAsync(credentials, hubId, folderContentItem.Value.links.self.href, context);
                }
                else
                {
                    // found an items!
                    await GetItemVersionsAsync(credentials, hubId, folderUrn, folderContentItem.Value.links.self.href, context);
                }

            }
        }

        private async Task GetItemVersionsAsync(Credentials credentials, string hubId, string folderUrn, string itemHref, PerformContext context)
        {
            await credentials.RefreshAsync();

            BackgroundJobClient metadataQueue = new BackgroundJobClient();
            IState state = new EnqueuedState("metadata");

            // the API SDK
            ItemsApi itemApi = new ItemsApi();
            itemApi.Configuration.AccessToken = credentials.TokenInternal;

            // extract the projectId & itemId from the href
            string[] idParams = itemHref.Split('/');
            string itemUrn = idParams[idParams.Length - 1];
            string projectId = idParams[idParams.Length - 3];

            var item = await itemApi.GetItemAsync(projectId, itemUrn);

            string versionUrn = (string)item.data.relationships.tip.data.id;
            string fileName = (string)item.data.attributes.displayName;
            string extension = fileName.Split(".").Last();

            if (extension != "dwg") return; // only DWG for now...

            context.WriteLine(string.Format("{0}: {1}", fileName, versionUrn));

            await ModelDerivativeHub.NotifyFileFound(_hubContext, hubId);
            metadataQueue.Create(() => ProcessFile(credentials.UserId, hubId, projectId, folderUrn, itemUrn, versionUrn, fileName, null), state);
        }

        public async Task ProcessFile(string userId, string hubId, string projectId, string folderUrn, string itemUrn, string versionUrn, string fileName, PerformContext console){
           await ModelDerivativeController.ProcessFileAsync(userId, hubId, projectId, folderUrn, itemUrn, versionUrn, fileName, console);
           await ModelDerivativeHub.NotifyFileComplete(_hubContext, hubId);
        }
    }
}