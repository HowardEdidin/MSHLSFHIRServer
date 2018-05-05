using Hl7.Fhir.Model;

namespace FHIR3APIApp.Models
{
	public class ResourceResponse
	{
		public ResourceResponse()
		{
			Response = -1;
		}

		public ResourceResponse(Resource resource, int resp)
		{
			Resource = resource;
			Response = resp;
		}

		public Resource Resource { get; set; }

		//-1 == Error, 0==Updated, 1==Created
		public int Response { get; set; }
	}
}