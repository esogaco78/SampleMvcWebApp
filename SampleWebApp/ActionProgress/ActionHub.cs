﻿#region licence
// The MIT License (MIT)
// 
// Filename: ActionHub.cs
// Date Created: 2014/05/28
// 
// Copyright (c) 2014 Jon Smith (www.selectiveanalytics.com & www.thereformedprogrammer.net)
// 
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
// 
// The above copyright notice and this permission notice shall be included in all
// copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
// SOFTWARE.
#endregion
using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;
using System.Web.Helpers;
using GenericServices.ActionComms;
using GenericServices.Actions;
using GenericServices.Logger;
using Microsoft.AspNet.SignalR;

namespace SampleWebApp.ActionProgress
{
    public delegate IActionHubDependencyResolver NewLifeTimeScope();

    public class ActionHub : Hub, IActionHubSend
    {
        private static readonly IGenericLogger Logger;

        static ActionHub()
        {
            Logger = GenericServices.GenericServicesConfig.GetLogger("ActionHub");
        }

        private static readonly ConcurrentDictionary<string, IHubControl> AllActionsRunning = new ConcurrentDictionary<string, IHubControl>();

        //--------------------------------------------------
        //public static for getting lifetime scoped dependecy resolver

        /// <summary>
        /// This should be set to the method to create a new lifetime scoped dependency injector
        /// </summary>
        public static NewLifeTimeScope LifeTimeScopeProvider { get; set; }

        #region methods called by JavaScript

        /// <summary>
        /// This is called by JavaScript to start a action which has already been set up.
        /// It calls the 'Started' method to inform the called when the action is started successfully,
        /// of calls 'Failed' if there was a problem.
        /// </summary>
        /// <param name="actionGuid">The actionGuid is used to look for a Action instance to run</param>
        public async Task StartAction(string actionGuid)
        {
            var actionRunner = GetActionRunner(actionGuid);

            if (actionRunner != null)
                //If this succeeds then this Hub is paused for the duration of the action
                await actionRunner.RunActionAsync(actionGuid, Context.ConnectionId, this);
        }

        /// <summary>
        /// This is called by JavaScript to cancels a running action
        /// It calls the 'Cancelled' method to inform the called when the action is cancelled successfully,
        /// of calls 'Failed' if there was a problem.
        /// </summary>
        /// <param name="actionGuid">The actionGuid is used to look for a Action instance</param>
        public void CancelAction(string actionGuid)
        {
            var actionRunner = GetActionRunner(actionGuid);

            if (actionRunner == null || actionRunner.CancelRunningAction(actionGuid)) 
                //It wasn't there or wasn't running so we send a cancelled message straght away to stop a potential hangup
                Stopped(actionRunner, ProgressMessage.CancelledMessage("Action had already finished (or there was a problem)."), null);
        }

        /// <summary>
        /// This is called by JavaScript at the end of the action
        /// </summary>
        /// <param name="actionGuid">The actionGuid is used to look for a Action instance to run</param>
        public void EndAction(string actionGuid)
        {
            //not now used, but left in
        }

        #endregion


        #region methods to send information to JavaScript

        /// <summary>
        /// This is returned to client when the action is started. It confirms to the user that all is OK
        /// </summary>
        /// <param name="actionRunner"></param>
        /// <param name="actionConfig">a string of flags on what options the action supports</param>
        public void Started(IHubControl actionRunner, string actionConfig)
        {
            Clients.Client(actionRunner.UserConnectionId).Started(actionRunner.ActionGuid, actionConfig);
        }

        /// <summary>
        /// This sents progress reports to the client with an optional message
        /// </summary>
        /// <param name="actionRunner"></param>
        /// <param name="percentageDone">should run between 0 and 100. If you don't know how long then need another way of doing this</param>
        /// <param name="message">optional message</param>
        public void Progress(IHubControl actionRunner, int percentageDone, ProgressMessage message)
        {
            Clients.Client(actionRunner.UserConnectionId).Progress(actionRunner.ActionGuid, percentageDone, message);
        }

        /// <summary>
        /// This is called when the action has stopped, either because it has finished, had an error or its been cancelled.
        /// </summary>
        /// <param name="actionRunner"></param>
        /// <param name="finalMessage">The finalMessage must be present as the MessageType property carries the type of stop</param>
        /// <param name="sendAsJson">This is object to send as json to the client. Will be null if errors.</param>
        public void Stopped(IHubControl actionRunner, ProgressMessage finalMessage, object sendAsJson)
        {
            //Note that SignalR uses the Json.Net for serialising data, not the .NET json serialiser. 
            Clients.Client(actionRunner.UserConnectionId).Stopped(actionRunner.ActionGuid, finalMessage, sendAsJson);
        }

        #endregion


        //--------------------------------------------------------

        /// <summary>
        /// It is possible the user may abort a job by moving away to another page. In this case
        /// we want to a) cancel their running action and b) remove that entry from the directory
        /// </summary>
        /// <returns></returns>
        public override Task OnDisconnected()
        {
            //we need to be careful here as things are async. The action may be removed by other means in parallel

            var actionIdsOfActionsInDict =
                AllActionsRunning.Values.Where(x => x.UserConnectionId == Context.ConnectionId)
                               .Select(x => x.ActionGuid)
                               .ToList();

            if (actionIdsOfActionsInDict.Count > 1)
                //There should have only been one action being run by user
                Logger.Error("There was more than one entry in the AllRunningActions which had the user connection. Software error.");

            foreach (var actionGuid in actionIdsOfActionsInDict)
            {
                IHubControl actionRunner;
                AllActionsRunning.TryGetValue(actionGuid, out actionRunner);        //due to async nature of actioning it could have been removed elsewhere
                if (actionRunner != null)
                {
                    actionRunner.CancelRunningAction(actionGuid);                   //make sure its cancelled
                    AllActionsRunning.TryRemove(actionGuid, out actionRunner);      //try to remove, but ok if it isn't there (due to async behaviour)
                }
            }

            return base.OnDisconnected();
        }


        //--------------------------------------------------------
        //public static helpers

        public static void SetActionRunner(IHubControl actionRunner)
        {
            AllActionsRunning[actionRunner.ActionGuid] = actionRunner;
        }

        public static void RemoveActionRunner(string actionGuid)
        {
            IHubControl oldValue;
            if (!AllActionsRunning.TryRemove(actionGuid, out oldValue))
                Logger.Warn("Attempted to remove actionGuid from dictionary but wasn't there. Could be concurrency issue.");
        }

        //--------------------------------------------------------
        //private helper

        private static IHubControl GetActionRunner(string actionGuid)
        {
            return AllActionsRunning[actionGuid];
        }
    }
}