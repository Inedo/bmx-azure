﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Xml;
using System.Xml.Linq;
using Inedo.BuildMaster.Data;
using Inedo.BuildMaster.Extensibility.Actions;
using Inedo.Serialization;

namespace Inedo.BuildMasterExtensions.Azure
{
    public abstract class AzureAction : RemoteActionBase
    {
        internal protected enum RequestType { Get, Post, Delete };

        internal AzureConfigurer TestConfigurer { get; set; }
        protected static string OperationVersion = "2012-03-01";
        protected static XNamespace ns = "http://schemas.microsoft.com/windowsazure";

        [Persistent]
        public AzureAuthentication ActionCredentials { get; set; }

        protected AzureConfigurer Configurer
        {
            get
            {
                if (this.IsExecuting)
                {
                    return this.GetExtensionConfigurer() as AzureConfigurer;
                }
                else
                {
                    var typ = typeof(AzureConfigurer);

                    var profiles = DB.ExtensionConfiguration_GetConfigurations(typ.FullName + "," + typ.Assembly.GetName().Name);

                    var configurer = profiles.FirstOrDefault(p => p.Default_Indicator) ?? profiles.FirstOrDefault();

                    if (configurer == null)
                        return null;

                    return (AzureConfigurer)Persistence.DeserializeFromPersistedObjectXml(configurer.Extension_Configuration);
                }
            }
        }

        protected AzureAuthentication Credentials => this.ActionCredentials ?? this.Configurer.Credentials;

        protected string ResolveLegacyPath(string legacyPath)
        {
            string resolvedPath = LegacyPathResolver.GetWorkingDirectory(this.Context.ExecutionId, this.Context.DeployableId, legacyPath);

            this.LogDebug("Resolved path: " + resolvedPath);

            return resolvedPath;
        }

        internal protected IDictionary<string, string> ListLocations()
        {
            Dictionary<string, string> retVal = new Dictionary<string, string>();
            var resp = AzureRequest(RequestType.Get, null, "https://management.core.windows.net/{0}/locations");
            if (HttpStatusCode.OK == resp.StatusCode)
            {
                XElement locations = resp.Document.Element(ns + "Locations");
                foreach (XElement location in locations.Elements(ns + "Location"))
                {
                    retVal.Add(location.Element(ns + "Name").Value, location.Element(ns + "DisplayName").Value);
                }
            }
            return retVal;
        }


        internal protected IDictionary<string, string> ListAffinityGroups()
        {
            Dictionary<string, string> retVal = new Dictionary<string, string>();
            var resp = AzureRequest(RequestType.Get, null, "https://management.core.windows.net/{0}/affinitygroups");
            if (HttpStatusCode.OK == resp.StatusCode)
            {
                XElement groups = resp.Document.Element(ns + "AffinityGroups");
                foreach (XElement group in groups.Elements(ns + "AffinityGroup"))
                {
                    retVal.Add(group.Element(ns + "Name").Value, group.Element(ns + "Description").Value);
                }
            }
            return retVal;
        }

        internal protected void WaitForRequestCompletion(string requestId)
        {
            while (true)
            {
                var azureResponse = this.AzureRequest(RequestType.Get, null, "https://management.core.windows.net/{0}/operations/{1}", requestId);

                if (azureResponse.StatusCode != HttpStatusCode.OK)
                {
                    this.LogError("HTTP Error {0} waiting for the completion of request {1}", azureResponse.StatusCode, requestId);
                    return;
                }
                else if (azureResponse.OperationStatus != AzureResponse.OperationStatusResult.InProgress)
                {
                    if (azureResponse.OperationStatus == AzureResponse.OperationStatusResult.Succeeded)
                        this.LogInformation("Finished waiting for request {0} successfully.", requestId);
                    else
                        this.LogInformation("Failed waiting for request {0}. Error code is {1} and message is: {2}", requestId, azureResponse.ErrorCode, azureResponse.ErrorMessage);

                    return;
                }

                Thread.Sleep(2000);
                this.ThrowIfCanceledOrTimeoutExpired();
            }
        }

        internal AzureResponse AzureRequest(RequestType requestType, string payload, string uriFormat, params object[] args)
        {
            var azureResponse = new AzureResponse();
            var uri = new Uri(string.Format(uriFormat, new object[] { this.Credentials.SubscriptionID }.Concat(args).ToArray()));
            this.LogDebug("Sending Azure API {0} request to \"{1}\"...", requestType.ToString().ToUpper(), uri);
            var req = (HttpWebRequest)HttpWebRequest.Create(uri);
            if (requestType == RequestType.Post)
                req.Method = "POST";
            else if (requestType == RequestType.Delete)
                req.Method = "DELETE";
            else
                req.Method = "GET";

            req.Headers.Add("x-ms-version", OperationVersion);
            req.ClientCertificates.Add(this.Credentials.Certificate);
            req.ContentType = "application/xml";
            if (!string.IsNullOrEmpty(payload))
            {
                this.LogDebug("Writing request data...");
                var buffer = Encoding.UTF8.GetBytes(payload);
                req.ContentLength = buffer.Length;
                Stream reqStream = req.GetRequestStream();
                reqStream.Write(buffer, 0, buffer.Length);
                reqStream.Close();
            }
            HttpWebResponse resp;
            try
            {
                resp = (HttpWebResponse)req.GetResponse();
            }
            catch (WebException ex)
            {
                resp = (HttpWebResponse)ex.Response;
            }
            azureResponse.StatusCode = resp.StatusCode;
            azureResponse.Headers = resp.Headers;
            if (resp.ContentLength > 0)
            {
                using (XmlReader reader = XmlReader.Create(resp.GetResponseStream()))
                {
                    this.LogDebug("Parsing Azure API XML response...");

                    azureResponse.Document = XDocument.Load(reader);
                    azureResponse.ErrorMessage = (string)azureResponse.Document.Descendants(ns + "Message").FirstOrDefault();
                    azureResponse.ErrorCode = (string)azureResponse.Document.Descendants(ns + "Code").FirstOrDefault();
                    AzureResponse.OperationStatusResult status;
                    var statusElement = azureResponse.Document.Root.Element(ns + "Status");
                    if (statusElement != null && Enum.TryParse<AzureResponse.OperationStatusResult>(statusElement.Value, true, out status))
                        azureResponse.OperationStatus = status;

                    this.LogDebug("Azure API XML response parsed.");
                }
            }

            return azureResponse;
        }
    }
}
