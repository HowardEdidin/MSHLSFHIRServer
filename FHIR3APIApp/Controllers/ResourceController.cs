/* 
* 2017 Microsoft Corp
* 
* THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS “AS IS”
* AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO,
* THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE
* ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT OWNER OR CONTRIBUTORS BE LIABLE
* FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
* (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
* LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION)
* HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY,
* OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
* OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
*/

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
using FHIR3APIApp.Models;
using FHIR3APIApp.Providers;
using FHIR3APIApp.Security;
using FHIR3APIApp.Utils;
using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;
using Microsoft.Azure;

namespace FHIR3APIApp.Controllers
{
	[EnableCors("*", "*", "*")]
	[FhirAuthorize]
	[RoutePrefix("")]
	public class ResourceController : ApiController
	{
		private const string Fhircontenttypejson = "application/fhir+json;charset=utf-8";
		private const string Fhircontenttypexml = "application/fhir+xml;charset=utf-8";

		private readonly FhirJsonParser _jsonparser;
		//private readonly string _parsemode = CloudConfigurationManager.GetSetting("FHIRParserMode");

		private readonly IFhirStore _storage;
		private readonly FhirXmlParser _xmlparser;

		//TODO: Inject Storage Implementation
		public ResourceController(IFhirStore store)
		{
			var s = CloudConfigurationManager.GetSetting("FHIRParserMode");
			var strict = s == null || s.Equals("strict", StringComparison.CurrentCultureIgnoreCase);
			_storage = store;
			var parsersettings = new ParserSettings
			{
				AcceptUnknownMembers = !strict,
				AllowUnrecognizedEnums = !strict
			};
			_jsonparser = new FhirJsonParser(parsersettings);
			_xmlparser = new FhirXmlParser(parsersettings);
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
				var cv = await _storage.LoadFhirResource(p.Id, resourceType);
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
			var rslt = await _storage.UpsertFhirResource(p);
			return new ResourceResponse(p, rslt);
		}

		private async Task<HttpResponseMessage> Upsert(string resourceType, string headerid = null)
		{
			try
			{
				var raw = await Request.Content.ReadAsStringAsync();
				BaseFhirParser parser;
				if (IsContentTypeJson) parser = _jsonparser;
				else if (IsContentTypeXml) parser = _xmlparser;
				else throw new Exception("Invalid Content-Type must be application/fhir+json or application/fhir+xml");

			


				var reader = JsonDomFhirNavigator.Create(raw);

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
				var dbresp = await ProcessSingleResource(p, resourceType, IsMatchVersionId);
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
					await ProcessSingleResource(ec.Resource, Enum.GetName(typeof(ResourceType), ec.Resource.ResourceType));
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
		public async Task<HttpResponseMessage> Post(string resource)
		{
			return await Upsert(resource);
		}

		[HttpPut]
		[Route("{resource}")]
		public async Task<HttpResponseMessage> Put(string resource)
		{
			return await Upsert(resource);
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
					var query = FhirParmMapper.Instance.GenerateQuery(_storage, resource, nvc);
					searchrslt =
						await _storage.QueryFhirResource(query, resource, int.Parse(count), nextpage, long.Parse(querytotal));
					retVal = searchrslt.Resources;
					iqueryTotal = (int) searchrslt.Total;
				}
				else
				{
					retVal = new List<Resource>();
					var r = await _storage.LoadFhirResource(id, resource);
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
					var includes = await FhirHelper.ProcessIncludes(p, nvc, _storage);
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
			var retVal = await _storage.LoadFhirResource(id, resource);
			if (retVal != null)
			{
				await _storage.DeleteFhirResource(retVal);
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
		public async Task<HttpResponseMessage> PutWithId(string resource, string id)
		{
			return await Upsert(resource, id);
		}

		[HttpPost]
		[Route("{resource}/{id}")]
		public async Task<HttpResponseMessage> PostWIthId(string resource, string id)
		{
			return await Upsert(resource, id);
		}

		[HttpGet]
		[Route("{resource}/{id}")]
		public async Task<HttpResponseMessage> Get(string resource, string id)
		{
			if (Request.Method == HttpMethod.Post) return await Upsert(resource);
			if (Request.Method == HttpMethod.Put) return await Upsert(resource);

			var retVal = await _storage.LoadFhirResource(id, resource);
			var respval = SerializeResponse(retVal);
			var response = Request.CreateResponse(HttpStatusCode.OK);
			response.Headers.TryAddWithoutValidation("Accept", CurrentAcceptType);
			response.Content = new StringContent(respval, Encoding.UTF8);
			response.Content.Headers.LastModified = retVal.Meta.LastUpdated;
			response.Headers.Add("ETag", "W/\"" + retVal.Meta.VersionId + "\"");

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
			var item = _storage.HistoryStore.GetResourceHistoryItem(resource, id, vid);
			{
				var retVal = (Resource) _jsonparser.Parse(item, FhirHelper.ResourceTypeFromString(resource));
				if (retVal != null) respval = SerializeResponse(retVal);
				response = Request.CreateResponse(HttpStatusCode.OK);
				response.Headers.TryAddWithoutValidation("Accept", CurrentAcceptType);
				response.Headers.Add("ETag", "W/\"" + retVal.Meta.VersionId + "\"");
				response.Content = new StringContent(respval, Encoding.UTF8);
				response.Content.Headers.LastModified = retVal.Meta.LastUpdated;
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
			var history = _storage.HistoryStore.GetResourceHistory(resource, id);
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
				var r = (Resource) _jsonparser.Parse(h, FhirHelper.ResourceTypeFromString(resource));
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

		private static string SerializeResponse(Base retVal)
		{
			if (CurrentAcceptType.ToLower().Contains("json"))
			{
				var serialize = new FhirJsonSerializer();

				return serialize.SerializeToString(retVal);
			}

			if (CurrentAcceptType.ToLower().Contains("xml"))
			{
				var serialize = new FhirXmlSerializer();
				return serialize.SerializeToString(retVal);
			}

			throw new HttpException((int) HttpStatusCode.NotAcceptable, "Accept Type not Supported must be */xml or */json");
		}

		protected string GetBaseUrl()
		{
			return Request.RequestUri.Scheme + "://" + Request.RequestUri.Host +
			       (Request.RequestUri.Port != 80 || Request.RequestUri.Port != 443 ? ":" + Request.RequestUri.Port : "");
		}
	}
}