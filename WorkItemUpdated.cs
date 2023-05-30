﻿using Microsoft.AspNetCore.Routing;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.Azure.WebJobs.Host;
using Microsoft.Azure.WebJobs;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace ADOSync
{
    public static class WorkItemUpdated
    {
        [FunctionName("WorkItemUpdated")]
        public static async Task<HttpResponseMessage> Run([HttpTrigger(AuthorizationLevel.Function, "get", "post", Route = null)] HttpRequestMessage req, ILogger log)
        {
            log.LogInformation($"++++++++++WorkItemUpdate was triggered!");
            HttpContent requestContent = req.Content;
            string jsonContent = requestContent.ReadAsStringAsync().Result;
            log.LogInformation(jsonContent);

            JObject originalWI = JObject.Parse(jsonContent);
            string originalWorkItemId = Uri.UnescapeDataString((string)originalWI["resource"]["workItemId"]);
            log.LogInformation(originalWorkItemId);

            string sourceVSTS = Uri.UnescapeDataString((string)originalWI["resourceContainers"]["collection"]["baseUrl"]);
            if (sourceVSTS.LastIndexOf('/') == sourceVSTS.Length - 1)
            {
                sourceVSTS = sourceVSTS.Substring(0, sourceVSTS.Length - 1);
            }
            string sourceTeamProject = Uri.UnescapeDataString((string)originalWI["resource"]["revision"]["fields"]["System.TeamProject"]);


            string oldValueRev = "";
            try
            {
                oldValueRev = Uri.UnescapeDataString((string)originalWI["resource"]["fields"]["System.Rev"]["oldValue"]);
                log.LogInformation(oldValueRev);
            }
            catch (Exception)
            {
                log.LogInformation("Revision didn't change");
                return req.CreateResponse(HttpStatusCode.OK, "Revision didn't change");
            }

            string destVSTS;
            string destPrj;
            string destPAT;

            Utility.SetVSTSAccountInfo("", sourceTeamProject, out destVSTS, out destPrj, out destPAT);

            string destWorkItemId = await Utility.GetDestinationWorkItemId(destVSTS, destPAT, destPrj, originalWorkItemId, log);

            if (destWorkItemId == "")
            {
                log.LogInformation("WorkItem to be updated not found... Perhaps we should create it... :-)");
                return req.CreateResponse(HttpStatusCode.OK, "WorkItem to be updated not found... Perhaps we should create it... :-)");
            }
            else
            {
                try
                {
                    var jsonPatch = new JsonPatch();
                    if (originalWI["resource"]["fields"] == null)
                    {
                        log.LogInformation("+++No Fields Changed+++");
                    }
                    else
                    {
                        foreach (JProperty property in ((JObject)originalWI["resource"]["fields"]).Properties())
                        {
                            try
                            {
                                log.LogInformation(property.Name + " - " + Utility.JSONEncode((string)originalWI["resource"]["fields"][property.Name]["newValue"]));
                            }
                            catch (Exception)
                            {
                                log.LogInformation("No newvalue -------should remove old one-----");
                                break;
                            }
                            if (property.Name == "System.Tags")
                            {
                                jsonPatch.Add("add", "/fields/System.Tags", null, Utility.JSONEncode((string)originalWI["resource"]["fields"][property.Name]["newValue"] + "; OriginalID=" + originalWorkItemId));
                            }
                            else if (property.Name.Contains("Kanban.Column") || property.Name.Contains("System.BoardColum"))
                            {
                                log.LogInformation("Skip Kanban.Column(s)");
                            }
                            else if (property.Name == "Microsoft.VSTS.TCM.TestSuiteAudit")
                            {
                                string value = (string)originalWI["resource"]["fields"][property.Name]["newValue"];

                                string[] numbers = Regex.Split(value, @"\D+");
                                foreach (string origWI in numbers)
                                {
                                    int prova;
                                    if (Int32.TryParse(origWI, out prova))
                                    {
                                        string destWI = await Utility.GetDestinationWorkItemId(destVSTS, destPAT, destPrj, origWI, log);
                                        if (destWI != "")
                                        {
                                            value = value.Replace(origWI, destWI);
                                            if (value.StartsWith("Removed these test suites:"))
                                            {
                                                await Utility.DeleteTestSuite(destVSTS, destPAT, destPrj, destWI, log);
                                            }
                                            if (value.StartsWith("Removed these test cases:"))
                                            {
                                                await Utility.RemoveTestCaseFromTestSuite(destVSTS, destPAT, destPrj, destWorkItemId, destWI, log);
                                            }
                                        }
                                        if (value.StartsWith("Added these test cases:"))
                                        {
                                            string originalTestCase = await Utility.GetTestCaseById(sourceVSTS, destPAT, sourceTeamProject, originalWorkItemId, origWI, log);
                                            string destinationTestCase = await Utility.UpdateTestCase(destVSTS, destPAT, destPrj, originalTestCase, log);
                                            await Utility.AddTestCaseToTestSuite(destVSTS, destPAT, destPrj, destWorkItemId, destWI, destinationTestCase, log);
                                        }
                                    }
                                }
                                jsonPatch.Add("add", "/fields/" + property.Name, null, value);
                            }
                            else
                            {
                                jsonPatch.ParseAndAddProperty(property.Name, (string)originalWI["resource"]["fields"][property.Name]["newValue"]);
                            }
                        }
                    }
                    if (originalWI["resource"]["relations"] == null)
                    {
                        log.LogInformation("+++No Relations+++");
                    }
                    else
                    {
                        if (originalWI["resource"]["relations"]["added"] == null)
                        {
                            log.LogInformation("+++No Added Relations+++");
                        }
                        else
                        {
                            JArray relations = (JArray)originalWI["resource"]["relations"]["added"];
                            foreach (JObject relation in relations.Children())
                            {
                                foreach (JProperty property in relation.Properties())
                                {
                                    log.LogInformation(property.Name + " - " + Utility.JSONEncode(property.Value.ToString()));
                                }

                                string rel = relation.Properties().FirstOrDefault(x => x.Name == "rel").Value.ToString();
                                string relUrl = "";

                                if (rel == "AttachedFile")
                                {
                                    // attachments

                                    relUrl = relation.Properties().FirstOrDefault(x => x.Name == "url").Value.ToString();
                                    byte[] attachment = await Utility.GetSourceAttachment(relUrl + "?api-version=4.1", destPAT, log);
                                    log.LogInformation("Attachment length: " + attachment.Length.ToString());

                                    string fileName = ((JObject)relation.Properties().FirstOrDefault(x => x.Name == "attributes").Value).Properties().FirstOrDefault(x => x.Name == "name").Value.ToString();
                                    log.LogInformation(fileName);
                                    string jsonAttach = await Utility.CreateAttachment(destVSTS, destPAT, fileName, attachment, log);
                                    JObject jsonObj = JObject.Parse(jsonAttach);

                                    relUrl = Uri.UnescapeDataString((string)jsonObj["url"]);
                                    log.LogInformation(relUrl);

                                }
                                else
                                {
                                    relUrl = await Utility.GetLinkedUrl(destVSTS, destPAT, destPrj, relation, log);
                                }

                                jsonPatch.AddCollection("add", "/relations/-", "",
                                    "{" +
                                    "  \"rel\": \"" + rel + "\"," +
                                    "  \"url\": \"" + relUrl + "\"," +
                                    "  \"attributes\": " + relation.Properties().FirstOrDefault(x => x.Name == "attributes").Value.ToString() +
                                    "}");
                            }
                        }
                        if (originalWI["resource"]["relations"]["removed"] == null)
                        {
                            log.LogInformation("+++No Removed Relations+++");
                        }
                        else
                        {
                            string destinationWorkItem = await Utility.GetDestinationWorkItem(destVSTS, destPAT, destPrj, destWorkItemId, log);
                            JObject destinationWI = JObject.Parse(destinationWorkItem);
                            JArray destinationRelations = (JArray)destinationWI["relations"];

                            if (destinationRelations == null)
                            {
                                log.LogInformation("+++No Relations In Destination WorkItem+++");
                            }
                            else
                            {

                                JArray relations = (JArray)originalWI["resource"]["relations"]["removed"];
                                foreach (JObject relation in relations.Children())
                                {
                                    int relationId = -1;
                                    bool found = false;

                                    foreach (JObject destRelation in destinationRelations.Children())
                                    {
                                        relationId += 1;

                                        string relationRel = relation.Properties().FirstOrDefault(x => x.Name == "rel").Value.ToString();
                                        string relationURL = relation.Properties().FirstOrDefault(x => x.Name == "url").Value.ToString();

                                        if (relationRel == destRelation.Properties().FirstOrDefault(x => x.Name == "rel").Value.ToString())
                                        {
                                            if (relationRel.StartsWith("System.LinkTypes"))
                                            {
                                                if (await Utility.GetDestinationWorkItemId(destVSTS, destPAT, destPrj, Utility.GetWorkItemIdFromLinkUrl(relationURL), log) ==
                                                    Utility.GetWorkItemIdFromLinkUrl(destRelation.Properties().FirstOrDefault(x => x.Name == "url").Value.ToString()))
                                                {
                                                    found = true;
                                                    break;
                                                }
                                            }
                                            else if (relationRel == "AttachedFile")
                                            {
                                                string fileName = ((JObject)relation.Properties().FirstOrDefault(x => x.Name == "attributes").Value).Properties().FirstOrDefault(x => x.Name == "name").Value.ToString();
                                                string destFileName = ((JObject)destRelation.Properties().FirstOrDefault(x => x.Name == "attributes").Value).Properties().FirstOrDefault(x => x.Name == "name").Value.ToString();
                                                string fileSize = ((JObject)relation.Properties().FirstOrDefault(x => x.Name == "attributes").Value).Properties().FirstOrDefault(x => x.Name == "resourceSize").Value.ToString();
                                                string destFileSize = ((JObject)destRelation.Properties().FirstOrDefault(x => x.Name == "attributes").Value).Properties().FirstOrDefault(x => x.Name == "resourceSize").Value.ToString();

                                                string fileComment = "";
                                                try
                                                {
                                                    fileComment = ((JObject)relation.Properties().FirstOrDefault(x => x.Name == "attributes").Value).Properties().FirstOrDefault(x => x.Name == "comment").Value.ToString();
                                                }
                                                catch (Exception)
                                                { }

                                                string destFileComment = "";
                                                try
                                                {
                                                    destFileComment = ((JObject)destRelation.Properties().FirstOrDefault(x => x.Name == "attributes").Value).Properties().FirstOrDefault(x => x.Name == "comment").Value.ToString();
                                                }
                                                catch (Exception)
                                                { }

                                                if (fileName == destFileName && fileComment == destFileComment && fileSize == destFileSize)
                                                {
                                                    found = true;
                                                    break;
                                                }
                                            }
                                            else
                                            {
                                                if (relationURL ==
                                                    destRelation.Properties().FirstOrDefault(x => x.Name == "url").Value.ToString())
                                                {
                                                    found = true;
                                                    break;
                                                }
                                            }
                                        }
                                    }

                                    if (found)
                                    {
                                        jsonPatch.AddCollection("remove", "/relations/" + relationId.ToString(), "", "");
                                    }
                                }
                            }
                        }
                    }

                    if (jsonPatch.ToString() != "[]")
                    {
                        log.LogInformation(jsonPatch.ToString());
                        try
                        {
                            await Utility.UpdateWorkItem(destVSTS, destPAT, destPrj, destWorkItemId, jsonPatch, log);
                            return req.CreateResponse(HttpStatusCode.OK, "");
                        }
                        catch (Exception ex)
                        {
                            log.LogInformation(ex.Message);
                            return Utility.CreateErrorResponse(req, ex.Message);
                        }
                    }
                    else
                    {
                        log.LogInformation("+++ Empty JSON Patch +++");
                        return req.CreateResponse(HttpStatusCode.OK, "");
                    }
                }
                catch (Exception ex)
                {
                    log.LogInformation(ex.Message);
                    return Utility.CreateErrorResponse(req, ex.Message);
                }
            }
        }
    }
}