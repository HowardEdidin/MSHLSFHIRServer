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
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;
using Microsoft.Azure;
using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Blob;

namespace FHIR4APIApp.Providers
{
	public class ResourceThreadContext
	{
		public ResourceThreadContext(CloudBlobContainer blob, Resource r, string s)
		{
			BlobContainer = blob;
			Resource = r;
			Serialized = s;
		}

		public CloudBlobContainer BlobContainer { get; set; }
		public Resource Resource { get; set; }
		public string Serialized { get; set; }

		public void ThreadPoolCallback(object context)
		{
			var blob = BlobContainer;
			var r = Resource;
			var s = Serialized;
			var resource = Encoding.UTF8.GetBytes(s);
			var blockBlob =
				blob.GetBlockBlobReference(Enum.GetName(typeof(ResourceType), r.ResourceType) + "/" + r.Id + "/" +
				                           r.Meta.VersionId);
			using (var stream = new MemoryStream(resource, false))
			{
				blockBlob.UploadFromStream(stream);
			}
		}
	}

	public class AzureBlobHistoryStore : IFhirHistoryStore
	{
		private const string Container = "fhirhistory";
		private readonly CloudBlobContainer _blob;

		public AzureBlobHistoryStore()
		{
			var storageAccount = CloudStorageAccount.Parse(CloudConfigurationManager.GetSetting("StorageConnectionString"));
			// Create the table if it doesn't exist.
			var blobClient = storageAccount.CreateCloudBlobClient();
			_blob = blobClient.GetContainerReference(Container);
			_blob.CreateIfNotExists();
		}

		public string InsertResourceHistoryItem(Resource r)
		{
			try
			{
				var serialize = new FhirJsonSerializer();
				
				var s = serialize.SerializeToString(r);
				var rtc = new ResourceThreadContext(_blob, r, s);
				ThreadPool.QueueUserWorkItem(rtc.ThreadPoolCallback, 1);
				return s;
			}
			catch (Exception e)
			{
				Trace.TraceError("Error inserting history for resource: {0}-{1}-{2} Message:{3}",
					Enum.GetName(typeof(ResourceType), r.ResourceType), r.Id, r.Meta.VersionId, e.Message);
				return null;
			}
		}

		public void DeleteResourceHistoryItem(Resource r)
		{
			var blobSource =
				_blob.GetBlockBlobReference(Enum.GetName(typeof(ResourceType), r.ResourceType) + "/" + r.Id + "/" +
				                           r.Meta.VersionId);
			blobSource.DeleteIfExists();
		}

		public IEnumerable<string> GetResourceHistory(string resourceType, string resourceId)
		{
			//TODO: Add Paging
			var relativePath = resourceType + "/" + resourceId;

			return (from IListBlobItem blobItem in _blob.ListBlobs(relativePath, true, BlobListingDetails.All)
					.OfType<CloudBlob>()
					.OrderByDescending(b => b.Properties.LastModified)
				select GetFileNameFromBlobUri(blobItem.Uri).Split('/')
				into spl
				where spl.Length > 2
				select GetResourceHistoryItem(spl[0], spl[1], spl[2])
				into resource
				where resource != null
				select resource).ToList();
		}

		public string GetResourceHistoryItem(string resourceType, string resourceid, string versionid)
		{
			var blockBlob = _blob.GetBlockBlobReference(resourceType + "/" + resourceid + "/" + versionid);
			if (!blockBlob.Exists()) return null;
			string text;
			using (var memoryStream = new MemoryStream())
			{
				blockBlob.DownloadToStream(memoryStream);
				text = Encoding.UTF8.GetString(memoryStream.ToArray());
			}

			return text;
		}

		private static string GetFileNameFromBlobUri(Uri theUri)
		{
			var theFile = theUri.ToString();
			var dirIndex = theFile.IndexOf(Container, StringComparison.Ordinal);
			var oneFile = theFile.Substring(dirIndex + Container.Length + 1,
				theFile.Length - (dirIndex + Container.Length + 1));
			return oneFile;
		}
	}
}