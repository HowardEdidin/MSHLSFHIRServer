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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;
using FHIR4APIApp.Utils;
using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;
using Microsoft.Azure;
using Microsoft.Azure.Documents;
using Microsoft.Azure.Documents.Client;
using Microsoft.Azure.Documents.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Resource = Hl7.Fhir.Model.Resource;

namespace FHIR4APIApp.Providers
{
	public class AzureDocDbfhirStore : IFhirStore
	{
/*
		/// <summary>
		///     The maximum size of a FHIR Resource before attachment corelation or error
		/// </summary>
		private static int _maxdocsizebytes = 500000;
*/

		/// <summary>
		///     The Azure DocumentDB endpoint
		/// </summary>
		private static readonly string EndpointUri = CloudConfigurationManager.GetSetting("DBStorageEndPointUri");

		/// <summary>
		///     The primary key for the Azure DocumentDB account.
		/// </summary>
		private static readonly string PrimaryKey = CloudConfigurationManager.GetSetting("DBStoragePrimaryKey");

		/// <summary>
		///     The DBName for DocumentDB
		/// </summary>
		private static readonly string DbName = CloudConfigurationManager.GetSetting("FHIRDB");

		/// <summary>
		///     The Througput offer for the FHIRDB
		/// </summary>
		private static readonly string Dbdtu = CloudConfigurationManager.GetSetting("FHIRDBTHROUHPUT");

		/// <summary>
		///     The DocumentDB client instance.
		/// </summary>
		private static DocumentClient client;

		private readonly ConcurrentDictionary<string, string> collection = new ConcurrentDictionary<string, string>();

		private bool databasecreated;

		private readonly FhirJsonParser parser;

		public AzureDocDbfhirStore(IFhirHistoryStore history)
		{
			client = new DocumentClient(new Uri(EndpointUri), PrimaryKey, new ConnectionPolicy
			{
				ConnectionMode = ConnectionMode.Direct,
				ConnectionProtocol = Protocol.Tcp
			});
			
			HistoryStore = history;
			var ps = new ParserSettings
			{
				AcceptUnknownMembers = true,
				AllowUnrecognizedEnums = true
			};

			parser = new FhirJsonParser(ps);
		}

		public string SelectAllQuery => "select value c from c";

		public IFhirHistoryStore HistoryStore { get; }


		public async Task<bool> DeleteFhirResourceAsync(Resource r)
		{
			//TODO Implement Delete by Identity
			await CreateDocumentCollectionIfNotExistsAsync(DbName, Enum.GetName(typeof(ResourceType), r.ResourceType)).ConfigureAwait(false);
			try
			{
				await client.DeleteDocumentAsync(UriFactory.CreateDocumentUri(DbName,
					Enum.GetName(typeof(ResourceType), r.ResourceType), r.Id)).ConfigureAwait(false);
				return true;
			}
			catch (DocumentClientException)
			{
				//Trace.TraceError("Error deleting resource type: {0} Id: {1} Message: {2}", r.ResourceType, r.Id, de.Message);
				return false;
			}
		}

		public async Task<int> UpsertFhirResourceAsync(Resource r)
		{
			await CreateDocumentCollectionIfNotExistsAsync(DbName, Enum.GetName(typeof(ResourceType), r.ResourceType)).ConfigureAwait(false);
			var x = await CreateResourceIfNotExistsAsync(DbName, r).ConfigureAwait(false);
			return x;
		}

		public async Task<Resource> LoadFhirResourceAsync(string identity, string resourceType)
		{
			await CreateDocumentCollectionIfNotExistsAsync(DbName, resourceType).ConfigureAwait(false);
			var result = await LoadFhirResourceObjectAsync(DbName, resourceType, identity).ConfigureAwait(false);
			return result == null ? null :  ConvertDocument(result);
		}

		/// <inheritdoc />
		public async Task<ResourceQueryResult> QueryFhirResourceAsync(string query, string resourceType, int count = 100,
			string continuationToken = null, long querytotal = -1)
		{
			var retVal = new List<Resource>();
			await CreateDocumentCollectionIfNotExistsAsync(DbName, resourceType).ConfigureAwait(false);
			var options = new FeedOptions
			{
				MaxItemCount = count,
				RequestContinuation = FhirHelper.UrlBase64Decode(continuationToken)
			};
			var collectionId = UriFactory.CreateDocumentCollectionUri(DbName, resourceType);
			var docq = client.CreateDocumentQuery<Document>(collectionId, query, options).AsDocumentQuery();
			var rslt = await docq.ExecuteNextAsync<Document>().ConfigureAwait(false);
			//Get Totalcount first
			if (querytotal < 0) querytotal = rslt.Count;
			foreach (var doc in rslt) retVal.Add(ConvertDocument(doc));
			return new ResourceQueryResult(retVal, querytotal, FhirHelper.UrlBase64Encode(rslt.ResponseContinuation));
		}

		/// <inheritdoc />
		public async Task<bool> InitializeAsync(List<object> parms)
		{
			await client.OpenAsync().ConfigureAwait(false);
			return true;
		}

		private Resource ConvertDocument(Document doc)
		{
			var obj = (JObject) (dynamic) doc;
			obj.Remove("_rid");
			obj.Remove("_self");
			obj.Remove("_etag");
			obj.Remove("_attachments");
			obj.Remove("_ts");
			var rt = (string) obj["resourceType"];
			var t = (Resource) parser.Parse(obj.ToString(Formatting.None), FhirHelper.ResourceTypeFromString(rt));
			return t;
		}

		private async Task<ResourceResponse<Database>> CreateDatabaseIfNotExistsAsync(string databaseName)
		{
			if (databasecreated) return null;
			var x = await client.CreateDatabaseIfNotExistsAsync(new Database {Id = databaseName}).ConfigureAwait(false);
			databasecreated = true;
			return x;
		}

		private async Task<IResourceResponse<DocumentCollection>> CreateDocumentCollectionIfNotExistsAsync(string databaseName,
			string collectionName)
		{
			if (collection.ContainsKey(collectionName)) return null;
			await CreateDatabaseIfNotExistsAsync(databaseName).ConfigureAwait(false);
			var collectionDefinition = new DocumentCollection
			{
				Id = collectionName,
				IndexingPolicy = new IndexingPolicy(new RangeIndex(DataType.String) {Precision = -1})
			};

			var x = await client.CreateDocumentCollectionIfNotExistsAsync(
				UriFactory.CreateDatabaseUri(databaseName),
				collectionDefinition,
				new RequestOptions {OfferThroughput = int.Parse(Dbdtu)}).ConfigureAwait(false);
			collection.TryAdd(collectionName, collectionName);
			return x;
		}

		private static async Task<Document> LoadFhirResourceObjectAsync(string databaseName, string collectionName, string identity)
		{
			try
			{
				var response = await client.ReadDocumentAsync(UriFactory.CreateDocumentUri(databaseName, collectionName, identity)).ConfigureAwait(false);
				return response;
			}
			catch
			{
				//Trace.TraceError("Error loading resource: {0}-{1}-{2} Message: {3}",databaseName,collectionName,identity,de.Message);
				return null;
			}
		}

		private async Task<int> CreateResourceIfNotExistsAsync(string databaseName, Resource r)
		{
			var retstatus = -1; //Error

			try
			{
				if (r == null) return retstatus;
				var fh = HistoryStore.InsertResourceHistoryItem(r);
				if (fh == null) return retstatus;
				//Overflow remove attachments or error
				if (fh.Length > 500000) return retstatus;
				var obj = JObject.Parse(fh);
				var inserted = await client.UpsertDocumentAsync(
					UriFactory.CreateDocumentCollectionUri(databaseName, Enum.GetName(typeof(ResourceType), r.ResourceType)), obj).ConfigureAwait(false);
				retstatus = inserted.StatusCode == HttpStatusCode.Created ? 1 : 0;
				return retstatus;
			}
			catch (DocumentClientException)
			{
				//Trace.TraceError("Error creating resource: {0}-{1}-{2} Message: {3}", databaseName,Enum.GetName(typeof(Hl7.Fhir.Model.ResourceType),r.ResourceType),r.Id,de.Message);
				HistoryStore.DeleteResourceHistoryItem(r);
				//Trace.TraceInformation("Resource history entry for {0}-{1} version {2} rolledback due to document creation error.", Enum.GetName(typeof(Hl7.Fhir.Model.ResourceType), r.ResourceType), r.Id, r.Meta.VersionId);
				return retstatus;
			}
		}
	}
}