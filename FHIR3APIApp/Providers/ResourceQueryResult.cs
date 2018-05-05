using System.Collections.Generic;
using Hl7.Fhir.Model;

namespace FHIR3APIApp.Providers
{
	public class ResourceQueryResult
	{
		public ResourceQueryResult(IEnumerable<Resource> r, long total, string token)
		{
			Resources = r;
			Total = total;
			ContinuationToken = token;
		}

		public IEnumerable<Resource> Resources { get; set; }
		public long Total { get; set; }
		public string ContinuationToken { get; set; }
	}
}