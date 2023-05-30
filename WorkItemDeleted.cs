using Microsoft.AspNetCore.Routing;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.Azure.WebJobs.Host;
using Microsoft.Azure.WebJobs;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http.Headers;
using System.Net.Http;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace ADOSync
{
    public class WorkItemDeleted
    {
        [FunctionName("WorkItemDeleted")]
        public static async Task<HttpResponseMessage> Run([HttpTrigger(AuthorizationLevel.Function, "get", "post", Route = null)] HttpRequestMessage req, ILogger log)
        {
            log.LogInformation($"++++++++++WorkItemDelete was triggered!");

            HttpContent requestContent = req.Content;
            string jsonContent = requestContent.ReadAsStringAsync().Result;
            log.LogInformation(jsonContent);

            JObject originalWI = JObject.Parse(jsonContent);
            string workItemID = (string)originalWI["resource"]["id"];

            log.LogInformation("Original ID=" + workItemID);

            string destVSTS;
            string destPrj;
            string destPAT;

            Utility.SetVSTSAccountInfo("", "", out destVSTS, out destPrj, out destPAT);

            string destWorkItemID = await Utility.GetDestinationWorkItemId(destVSTS, destPAT, destPrj, workItemID, log);

            if (destWorkItemID == "")
            {
                log.LogInformation("WorkItem to be deleted not found...");
                return req.CreateResponse(HttpStatusCode.OK, "");
            }
            else
            {
                try
                {
                    await DeleteWorkItem(destVSTS, destPAT, destPrj, destWorkItemID, log);
                    return req.CreateResponse(HttpStatusCode.OK, "");
                }
                catch (Exception ex)
                {
                    if (ex.Message.Contains("404 (Not Found)"))
                    {
                        log.LogInformation("WorkItem to be deleted not found...");
                        return req.CreateResponse(HttpStatusCode.OK, "");
                    }
                    else
                    {
                        log.LogInformation(ex.Message);
                        return Utility.CreateErrorResponse(req, ex.Message);
                    }
                }
            }
        }

        private static async Task DeleteWorkItem(string VSTS, string PAT, string TeamProject, string WorkItemID, ILogger log)
        {
            // https://docs.microsoft.com/en-us/rest/api/vsts/wit/work%20items/delete?view=vsts-rest-4.1

            using (HttpClient client = new HttpClient())
            {
                SetAuthentication(client, PAT);

                var deleteUri = VSTS + "/" + TeamProject + "/_apis/wit/workitems/" + WorkItemID + "?api-version=4.1";
                log.LogInformation(deleteUri);

                using (HttpResponseMessage response = await client.DeleteAsync(deleteUri))
                {
                    response.EnsureSuccessStatusCode();
                    string responseBody = await response.Content.ReadAsStringAsync();
                    log.LogInformation(responseBody);
                }
            }
        }

        private static void SetAuthentication(HttpClient client, string PAT)
        {
            client.DefaultRequestHeaders.Accept.Add(
                                    new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json-patch+json"));

            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(
                    System.Text.ASCIIEncoding.ASCII.GetBytes(
                        string.Format("{0}:{1}", "", PAT))));
        }
    }
}

