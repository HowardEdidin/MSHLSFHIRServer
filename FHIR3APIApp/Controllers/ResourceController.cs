﻿#region

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Web;
using System.Web.Http;
using System.Web.Http.Cors;
using FHIR4APIApp.Models;
using FHIR4APIApp.Providers;
using FHIR4APIApp.Security;
using FHIR4APIApp.Utils;
using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;
using Microsoft.Azure;
#pragma warning disable 618

#endregion

namespace FHIR4APIApp.Controllers
{
	[EnableCors("*", "*", "*")]
	[FhirAuthorize]
	[RoutePrefix("")]
	public class ResourceController : ApiController
	{
		private const string Fhircontenttypejson = "application/fhir+json;charset=utf-8";
		private const string Fhircontenttypexml = "application/fhir+xml;charset=utf-8";
		private readonly FhirJsonParser jsonparser;

		private readonly IFhirStore storage;
		//private readonly string parsemode = CloudConfigurationManager.GetSetting("FHIRParserMode");

		private readonly FhirXmlParser xmlparser;

		//TODO: Inject Storage Implementation
		public ResourceController(IFhirStore store)
		{
			var s = CloudConfigurationManager.GetSetting("FHIRParserMode");
			var strict = s == null || s.Equals("strict", StringComparison.CurrentCultureIgnoreCase);
			storage = store;
			var parsersettings = new ParserSettings
			{
				AcceptUnknownMembers = !strict,
				AllowUnrecognizedEnums = !strict
			};
			jsonparser = new FhirJsonParser(parsersettings);
			xmlparser = new FhirXmlParser(parsersettings);
		}

		private static string CurrentAcceptType
		{
			get
			{
				var at = HttpContext.Current.Request.QueryString["_format"];
				if (string.IsNullOrEmpty(at))
					at = (HttpContext.Current.Request.AcceptTypes ?? throw new InvalidOperationException()).First();
				if (!string.IsNullOrEmpty(at))
				{
					if (at.Equals("text/html") || at.Equals("xml")) at = "application/xml";
					if (at.Equals("*/*") || at.Equals("json")) at = "application/json";
				}
				else
				{
					at = "application/json";
				}

				return at;
			}
		}

		private static string IsMatchVersionId => HttpContext.Current.Request.Headers["If-Match"];

		private static bool IsContentTypeJson => HttpContext.Current.Request.ContentType.ToLower().Contains("json");

		private static bool IsContentTypeXml => HttpContext.Current.Request.ContentType.ToLower().Contains("xml");
		private static bool IsAccceptTypeJson => CurrentAcceptType.ToLower().Contains("json");

		private async Task<ResourceResponse> ProcessSingleResource(Resource p, string resourceType,
			string matchversionid = null)
		{
			//Version conflict detection
			if (!string.IsNullOrEmpty(matchversionid))
			{
				var cv = await storage.LoadFhirResourceAsync(p.Id, resourceType).ConfigureAwait(false);
				if (cv == null || !matchversionid.Equals(cv.Meta.VersionId))
				{
					var oo = new OperationOutcome {Issue = new List<OperationOutcome.IssueComponent>()};
					var ic = new OperationOutcome.IssueComponent
					{
						Severity = OperationOutcome.IssueSeverity.Error,
						Code = OperationOutcome.IssueType.Exception,
						Diagnostics = "Version conflict current resource version of " + resourceType + "/" + p.Id + " is " +
						              cv.Meta.VersionId
					};
					oo.Issue.Add(ic);
					return new ResourceResponse(oo, -1);
				}
			}

			//Prepare for Insert/update and Version
			if (string.IsNullOrEmpty(p.Id)) p.Id = Guid.NewGuid().ToString();
			p.Meta = new Meta
			{
				VersionId = Guid.NewGuid().ToString(),
				LastUpdated = DateTimeOffset.UtcNow
			};
			var rslt = await storage.UpsertFhirResourceAsync(p).ConfigureAwait(false);
			return new ResourceResponse(p, rslt);
		}

		private async Task<HttpResponseMessage> Upsert(string resourceType, string headerid = null)
		{
			try
			{
			
				

				var raw = await Request.Content.ReadAsStringAsync().ConfigureAwait(false);
				BaseFhirParser parser;
				if (IsContentTypeJson) parser = jsonparser;
				else if (IsContentTypeXml) parser = xmlparser;
				else throw new Exception("Invalid Content-Type must be application/fhir+json or application/fhir+xml");
				var reader = IsContentTypeJson ? FhirJsonParser.CreateFhirReader(raw) : FhirXmlParser.CreateFhirReader(raw, false);
				var p = (Resource) parser.Parse(reader, FhirHelper.ResourceTypeFromString(resourceType));
				Enum.TryParse(resourceType, out ResourceType rt);
				if (p.ResourceType != rt)
				{
					var oo = new OperationOutcome {Issue = new List<OperationOutcome.IssueComponent>()};
					var ic = new OperationOutcome.IssueComponent
					{
						Severity = OperationOutcome.IssueSeverity.Error,
						Code = OperationOutcome.IssueType.Exception,
						Diagnostics = "Resource provide is not of type " + resourceType
					};
					oo.Issue.Add(ic);
					var respconf = Request.CreateResponse(HttpStatusCode.BadRequest);
					respconf.Content = new StringContent(SerializeResponse(oo), Encoding.UTF8);
					respconf.Content.Headers.LastModified = DateTimeOffset.Now;
					respconf.Headers.TryAddWithoutValidation("Accept", CurrentAcceptType);
					respconf.Content.Headers.TryAddWithoutValidation("Content-Type",
						IsAccceptTypeJson ? Fhircontenttypejson : Fhircontenttypexml);
					return respconf;
				}

				if (string.IsNullOrEmpty(p.Id) && headerid != null) p.Id = headerid;
				//Store resource regardless of type
				var dbresp = await ProcessSingleResource(p, resourceType, IsMatchVersionId).ConfigureAwait(false);
				p = dbresp.Resource;
				var response = Request.CreateResponse(dbresp.Response == 1 ? HttpStatusCode.Created : HttpStatusCode.OK);
				response.Content = new StringContent("", Encoding.UTF8);
				response.Content.Headers.LastModified = p.Meta.LastUpdated;
				response.Headers.Add("Location", Request.RequestUri.AbsoluteUri + (headerid == null ? "/" + p.Id : ""));
				response.Headers.Add("ETag", "W/\"" + p.Meta.VersionId + "\"");

				//Extract and Save each Resource in bundle if it's a batch type
				if (p.ResourceType != ResourceType.Bundle || ((Bundle) p).Type != Bundle.BundleType.Batch) return response;
				var source = (Bundle) p;
				/*Bundle results = new Bundle();
					results.Id = Guid.NewGuid().ToString();
					results.Type = Bundle.BundleType.Searchset;
					results.Total = source.Entry.Count();
					results.Link = new System.Collections.Generic.List<Bundle.LinkComponent>();
					results.Link.Add(new Bundle.LinkComponent() { Url = Request.RequestUri.AbsoluteUri, Relation = "original" });
					results.Entry = new System.Collections.Generic.List<Bundle.EntryComponent>();*/
				foreach (var ec in source.Entry)
				{
					var resourceResponse = await ProcessSingleResource(ec.Resource, Enum.GetName(typeof(ResourceType), ec.Resource.ResourceType)).
						ConfigureAwait(false);
					//results.Entry.Add(new Bundle.EntryComponent() { Resource = rslt.Resource, FullUrl = FhirHelper.GetFullURL(Request, rslt.Resource) });
				}

				return response;
			}
			catch (Exception e)
			{
				var oo = new OperationOutcome {Issue = new List<OperationOutcome.IssueComponent>()};
				var ic = new OperationOutcome.IssueComponent
				{
					Severity = OperationOutcome.IssueSeverity.Error,
					Code = OperationOutcome.IssueType.Exception,
					Diagnostics = e.Message
				};
				oo.Issue.Add(ic);
				var response = Request.CreateResponse(HttpStatusCode.BadRequest);
				response.Headers.TryAddWithoutValidation("Accept", CurrentAcceptType);
				response.Content = new StringContent(SerializeResponse(oo), Encoding.UTF8);
				response.Content.Headers.TryAddWithoutValidation("Content-Type",
					IsAccceptTypeJson ? Fhircontenttypejson : Fhircontenttypexml);
				response.Content.Headers.LastModified = DateTimeOffset.Now;
				return response;
			}
		}

		[HttpPost]
		[Route("{resource}")]
		public Task<HttpResponseMessage> Post(string resource)
		{
			return Upsert(resource);
		}

		[HttpPut]
		[Route("{resource}")]
		public Task<HttpResponseMessage> Put(string resource)
		{
			return  Upsert(resource);
		}

		[HttpGet]
		[Route("{resource}")]
		public async Task<HttpResponseMessage> Get(string resource)
		{
			string respval;
			if (Request.RequestUri.AbsolutePath.ToLower().EndsWith("metadata"))
			{
				respval = SerializeResponse(FhirHelper.GenerateCapabilityStatement(Request.RequestUri.AbsoluteUri));
			}
			else
			{
				var nvc = HttpUtility.ParseQueryString(Request.RequestUri.Query);
				var id = nvc["_id"];
				var nextpage = nvc["_nextpage"];
				var count = nvc["_count"] ?? "100";
				var querytotal = nvc["_querytotal"] ?? "-1";
				IEnumerable<Resource> retVal;
				ResourceQueryResult searchrslt = null;
				int iqueryTotal;
				if (string.IsNullOrEmpty(id))
				{
					var query = FhirParmMapper.Instance.GenerateQuery(storage, resource, nvc);
					searchrslt =
						await storage.QueryFhirResourceAsync(query, resource, int.Parse(count), nextpage, long.Parse(querytotal)).
							ConfigureAwait(false);
					retVal = searchrslt.Resources;
					iqueryTotal = (int) searchrslt.Total;
				}
				else
				{
					retVal = new List<Resource>();
					var r = await storage.LoadFhirResourceAsync(id, resource).ConfigureAwait(false);
					if (r != null) ((List<Resource>) retVal).Add(r);
					iqueryTotal = retVal.Count();
				}

				var baseurl = Request.RequestUri.Scheme + "://" + Request.RequestUri.Authority + "/" + resource;
				var results = new Bundle
				{
					Id = Guid.NewGuid().ToString(),
					Type = Bundle.BundleType.Searchset,
					Total = iqueryTotal,
					Link = new List<Bundle.LinkComponent>()
				};
				var qscoll = Request.RequestUri.ParseQueryString();
				qscoll.Remove("_count");
				qscoll.Remove("_querytotal");
				qscoll.Add("_querytotal", searchrslt.Total.ToString());
				qscoll.Add("_count", count);

				results.Link.Add(new Bundle.LinkComponent {Url = baseurl + "?" + qscoll, Relation = "self"});

				if (searchrslt.ContinuationToken != null)
				{
					qscoll.Remove("_nextpage");
					qscoll.Add("_nextpage", searchrslt.ContinuationToken);
					results.Link.Add(new Bundle.LinkComponent {Url = baseurl + "?" + qscoll, Relation = "next"});
				}

				results.Entry = new List<Bundle.EntryComponent>();
				var match = new Bundle.SearchComponent {Mode = Bundle.SearchEntryMode.Match};
				var include = new Bundle.SearchComponent {Mode = Bundle.SearchEntryMode.Include};
				foreach (var p in retVal)
				{
					results.Entry.Add(new Bundle.EntryComponent
					{
						Resource = p,
						FullUrl = FhirHelper.GetFullUrl(Request, p),
						Search = match
					});
					var includes = await FhirHelper.ProcessIncludesAsync(p, nvc, storage).ConfigureAwait(false);
					foreach (var r in includes)
						results.Entry.Add(new Bundle.EntryComponent
						{
							Resource = r,
							FullUrl = FhirHelper.GetFullUrl(Request, r),
							Search = include
						});
				}

				respval = SerializeResponse(results);
			}

			var response = Request.CreateResponse(HttpStatusCode.OK);
			response.Headers.TryAddWithoutValidation("Accept", CurrentAcceptType);

			response.Content = new StringContent(respval, Encoding.UTF8);
			response.Content.Headers.TryAddWithoutValidation("Content-Type",
				IsAccceptTypeJson ? Fhircontenttypejson : Fhircontenttypexml);
			return response;
		}

		[HttpDelete]
		[Route("{resource}/{id}")]
		public async Task<HttpResponseMessage> Delete(string resource, string id)
		{
			HttpResponseMessage response;
			const string respval = "";
			var retVal = await storage.LoadFhirResourceAsync(id, resource).ConfigureAwait(false);
			if (retVal != null)
			{
				await storage.DeleteFhirResourceAsync(retVal).ConfigureAwait(false);
				response = Request.CreateResponse(HttpStatusCode.NoContent);
				response.Headers.TryAddWithoutValidation("Accept", CurrentAcceptType);

				response.Content = new StringContent(respval, Encoding.UTF8);
				response.Headers.Add("ETag", "W/\"" + retVal.Meta.VersionId + "\"");
			}
			else
			{
				response = Request.CreateResponse(HttpStatusCode.NotFound);
				response.Content = new StringContent("", Encoding.UTF8);
			}

			response.Content.Headers.TryAddWithoutValidation("Content-Type",
				IsAccceptTypeJson ? Fhircontenttypejson : Fhircontenttypexml);
			return response;
		}

		[HttpPut]
		[Route("{resource}/{id}")]
		public Task<HttpResponseMessage> PutWithId(string resource, string id)
		{
			return Upsert(resource, id);
		}

		[HttpPost]
		[Route("{resource}/{id}")]
		public Task<HttpResponseMessage> PostWIthId(string resource, string id)
		{
			return Upsert(resource, id);
		}

		[HttpGet]
		[Route("{resource}/{id}")]
		public async Task<HttpResponseMessage> Get(string resource, string id)
		{
			if (Request.Method == HttpMethod.Post) return await Upsert(resource).ConfigureAwait(false);
			if (Request.Method == HttpMethod.Put) return await Upsert(resource).ConfigureAwait(false);

			HttpResponseMessage response;
			var retVal = await storage.LoadFhirResourceAsync(id, resource).ConfigureAwait(false);
			if (retVal != null)
			{
				var respval = SerializeResponse(retVal);
				response = Request.CreateResponse(HttpStatusCode.OK);
				response.Headers.TryAddWithoutValidation("Accept", CurrentAcceptType);
				response.Content = new StringContent(respval, Encoding.UTF8);
				response.Content.Headers.LastModified = retVal.Meta.LastUpdated;
				response.Headers.Add("ETag", "W/\"" + retVal.Meta.VersionId + "\"");
			}
			else
			{
				response = Request.CreateResponse(HttpStatusCode.NotFound);
				response.Content = new StringContent("", Encoding.UTF8);
			}

			response.Content.Headers.TryAddWithoutValidation("Content-Type",
				IsAccceptTypeJson ? Fhircontenttypejson : Fhircontenttypexml);
			return response;
		}

		// GET: Historical Speciic Version
		[HttpGet]
		[Route("{resource}/{id}/_history/{vid}")]
		public HttpResponseMessage GetHistory(string resource, string id, string vid)
		{
			HttpResponseMessage response;
			var respval = "";
			var item = storage.HistoryStore.GetResourceHistoryItem(resource, id, vid);
			if (item != null)
			{
				var retVal = (Resource) jsonparser.Parse(item, FhirHelper.ResourceTypeFromString(resource));
				if (retVal != null) respval = SerializeResponse(retVal);
				response = Request.CreateResponse(HttpStatusCode.OK);
				response.Headers.TryAddWithoutValidation("Accept", CurrentAcceptType);
				response.Headers.Add("ETag", "W/\"" + retVal.Meta.VersionId + "\"");
				response.Content = new StringContent(respval, Encoding.UTF8);
				response.Content.Headers.LastModified = retVal.Meta.LastUpdated;
			}
			else
			{
				response = Request.CreateResponse(HttpStatusCode.NotFound);
				response.Content = new StringContent("", Encoding.UTF8);
			}

			response.Content.Headers.TryAddWithoutValidation("Content-Type",
				IsAccceptTypeJson ? Fhircontenttypejson : Fhircontenttypexml);
			return response;
		}

		// GET: Historical Speciic Version
		[HttpGet]
		[Route("{resource}/{id}/_history")]
		public HttpResponseMessage GetHistoryComplete(string resource, string id)
		{
			var history = storage.HistoryStore.GetResourceHistory(resource, id);
			//Create Return Bundle
			var results = new Bundle
			{
				Id = Guid.NewGuid().ToString(),
				Type = Bundle.BundleType.History,
				Total = history.Count(),
				Link = new List<Bundle.LinkComponent>
				{
					new Bundle.LinkComponent
					{
						Url = Request.RequestUri.GetLeftPart(UriPartial.Authority),
						Relation = "self"
					}
				},
				Entry = new List<Bundle.EntryComponent>()
			};
			//Add History Items to Bundle
			foreach (var h in history)
			{
				//todo
				var r = (Resource) jsonparser.Parse(h, FhirHelper.ResourceTypeFromString(resource));
				results.Entry.Add(new Bundle.EntryComponent {Resource = r, FullUrl = FhirHelper.GetFullUrl(Request, r)});
			}


			//Serialize and Return Bundle
			var respval = SerializeResponse(results);
			var response = Request.CreateResponse(HttpStatusCode.OK);
			response.Headers.TryAddWithoutValidation("Accept", CurrentAcceptType);

			response.Content = new StringContent(respval, Encoding.UTF8);
			response.Content.Headers.TryAddWithoutValidation("Content-Type",
				IsAccceptTypeJson ? Fhircontenttypejson : Fhircontenttypexml);
			return response;
		}

		private string SerializeResponse(Base retVal)
		{

			if (CurrentAcceptType.ToLower().Contains("json"))
				return new FhirJsonSerializer().SerializeToString(retVal);
			if (CurrentAcceptType.ToLower().Contains("xml"))
				return new FhirXmlSerializer().SerializeToString((retVal));
			throw new HttpException((int) HttpStatusCode.NotAcceptable, "Accept Type not Supported must be */xml or */json");
		}

		protected string GetBaseUrl()
		{
			return Request.RequestUri.Scheme + "://" + Request.RequestUri.Host +
			       (Request.RequestUri.Port != 80 || Request.RequestUri.Port != 443 ? ":" + Request.RequestUri.Port : "");
		}
	}
}