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
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Web;
using FHIR4APIApp.Providers;
using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;
using Newtonsoft.Json.Linq;

namespace FHIR4APIApp.Utils
{
	public static class FhirHelper
	{
		public static string UrlBase64Encode(string plainText)
		{
			if (plainText == null) return null;
			var plainTextBytes = Encoding.UTF8.GetBytes(plainText);
			return HttpServerUtility.UrlTokenEncode(plainTextBytes);
		}

		public static string UrlBase64Decode(string base64EncodedData)
		{
			return base64EncodedData == null
				? null
				: Encoding.UTF8.GetString(HttpServerUtility.UrlTokenDecode(base64EncodedData) ??
				                          throw new InvalidOperationException());
		}

		public static CapabilityStatement GenerateCapabilityStatement(string url)
		{
			var cs = new CapabilityStatement
			{
				Name = "Azure HLS Team API Application FHIR Server",
				Status = PublicationStatus.Draft,
				Experimental = true,
				Publisher = "Microsoft Corporation",
				FhirVersion = "R4.Core  0.95.0.0",
				Format = new[] {"json", "xml"},
				Contact = new List<ContactDetail>()
			};

			// cs.AcceptUnknown = CapabilityStatement.UnknownContentCode.Both;
			var cc = new ContactDetail
			{
				Name = "Steve Ordahl",
				Telecom = new List<ContactPoint>
				{
					new ContactPoint(ContactPoint.ContactPointSystem.Email, ContactPoint.ContactPointUse.Work,
						"stordahl@microsoft.com")
				}
			};
			cs.Contact.Add(cc);
			cs.Kind = CapabilityStatement.CapabilityStatementKind.Instance;
			cs.Date = "2018-05-06";
			cs.Description =
				new Markdown("This is the FHIR capability statement for the HLS Team API Application FHIR Server 3.0.1");
			cs.Software =
				new CapabilityStatement.SoftwareComponent
				{
					Name = "Experimental Microsoft HLS Team FHIR Server API App",
					Version = "0.9.1",
					ReleaseDate = "2018-05-06"
				};
			cs.Implementation = new CapabilityStatement.ImplementationComponent {Description = "MSHLS Experimental FHIR Server"};
			var endpos = url.ToLower().LastIndexOf("/metadata", StringComparison.Ordinal);
			if (endpos > -1) url = url.Substring(0, endpos);
			cs.Implementation.Url = url;

			var rc = new CapabilityStatement.RestComponent
			{
				Mode = CapabilityStatement.RestfulCapabilityMode.Server,
				Security = new CapabilityStatement.SecurityComponent
				{
					Service = new List<CodeableConcept>
					{
						new CodeableConcept("http://hl7.org/fhir/restful-security-service", "SMART-on-FHIR",
							"OAuth2 using SMART-on-FHIR profile (see http://docs.smarthealthit.org)")
					},
					Extension = new List<Extension>()
				}
			};

			//security profile
			var oauthex = new Extension
			{
				Extension = new List<Extension>(),
				Url = "http://fhir-registry.smarthealthit.org/StructureDefinition/oauth-uris"
			};
			oauthex.Extension.Add(new Extension("token",
				new FhirUri("https://login.microsoftonline.com/microsoft.onmicrosoft.com/oauth2/token")));
			oauthex.Extension.Add(new Extension("authorize",
				new FhirUri("https://login.microsoftonline.com/microsoft.onmicrosoft.com/oauth2/authorize")));
			rc.Security.Extension.Add(oauthex);
			rc.Security.Cors = true;
			//All controller resources 
			var supported = Enum.GetValues(typeof(ResourceType));
			foreach (ResourceType k in supported)
			{
				var rescomp = new CapabilityStatement.ResourceComponent
				{
					Type = k,
					Versioning = CapabilityStatement.ResourceVersionPolicy.Versioned,
					Interaction = new List<CapabilityStatement.ResourceInteractionComponent>
					{
						new CapabilityStatement.ResourceInteractionComponent {Code = CapabilityStatement.TypeRestfulInteraction.Create},
						new CapabilityStatement.ResourceInteractionComponent {Code = CapabilityStatement.TypeRestfulInteraction.Update},
						new CapabilityStatement.ResourceInteractionComponent {Code = CapabilityStatement.TypeRestfulInteraction.Delete},
						new CapabilityStatement.ResourceInteractionComponent {Code = CapabilityStatement.TypeRestfulInteraction.Read},
						new CapabilityStatement.ResourceInteractionComponent {Code = CapabilityStatement.TypeRestfulInteraction.Vread}
					}
				};
				rc.Resource.Add(rescomp);
			}

			cs.Rest.Add(rc);
			return cs;
		}

		public static Type ResourceTypeFromString(string resourceType)
		{
			return Type.GetType("Hl7.Fhir.Model." + resourceType + ",Hl7.Fhir.R4.Core");
		}

		public static string GetResourceTypeString(Resource r)
		{
			return Enum.GetName(typeof(ResourceType), r.ResourceType);
		}

		public static string GetFullUrl(HttpRequestMessage request, Resource r)
		{
			try
			{
				var baseUri = new Uri(request.RequestUri.AbsoluteUri.Replace(request.RequestUri.PathAndQuery, string.Empty));
				var resourceFullPath =
					new Uri(baseUri, VirtualPathUtility.ToAbsolute("~/" + GetResourceTypeString(r) + "/" + r.Id));
				return resourceFullPath.ToString();
			}
			catch
			{
				return null;
			}
		}

		public static Patient PatientName(string standardname, HumanName.NameUse? use, Patient pat)
		{
			var family = standardname.Split(',');
			string[] given = null;
			if (family.Length > 1) given = family[1].Split(' ');
			if (pat.Name == null) pat.Name = new List<HumanName>();
			pat.Name.Add(new HumanName {Use = use, Family = family[0], Given = given});
			return pat;
		}

		public static string AppendWhereClause(string wc, string expression)
		{
			var retVal = wc;
			if (string.IsNullOrEmpty(wc)) retVal = " where " + expression;
			retVal = retVal + " AND " + expression;
			return retVal;
		}

		public static Identifier FindIdentifier(List<Identifier> ids, string system, string type)
		{
			Identifier retVal = null;
			foreach (var i in ids)
				if (i.System.Equals(system, StringComparison.CurrentCultureIgnoreCase) &&
				    i.Type.Coding[0].Code.Equals(type, StringComparison.CurrentCultureIgnoreCase))
				{
					retVal = i;
					break;
				}

			return retVal;
		}

		public static Resource StripAttachment(Resource source)
		{
			foreach (var prop in source.GetType().GetProperties())
				Console.WriteLine("{0}={1}", prop.Name, prop.GetValue(source, null));
			return null;
		}

		public static async Task<string> Read(HttpRequestMessage req)
		{
			using (var contentStream = await req.Content.ReadAsStreamAsync())
			{
				contentStream.Seek(0, SeekOrigin.Begin);
				using (var sr = new StreamReader(contentStream))
				{
					return sr.ReadToEnd();
				}
			}
		}

		public static async Task<List<Resource>> ProcessIncludes(Resource source, NameValueCollection parms, IFhirStore store)
		{
			var retVal = new List<Resource>();
			var includeparm = parms["_include"];
			if (!string.IsNullOrEmpty(includeparm))
			{
				var serialize = new FhirJsonSerializer();


				var j = serialize.SerializeToString(source);
				var incs = includeparm.Split(',');
				foreach (var t in incs)
				{
					var isinstance = false;
					var s = t.Split(':');
					if (s.Length <= 1) continue;
					var prop = s[1];
					JToken x = null;
					try
					{
						if (prop.Equals("substance"))
						{
							x = j[Convert.ToInt32("suspectEntity")];
							isinstance = true;
						}
						else
						{
							x = j[Convert.ToInt32(prop)];
						}
					}
					catch
					{
						// ignored
					}

					if (x == null) continue;
					for (var i = 0; i < x.Count(); i++)
					{
						var x1 = x.Type == JTokenType.Array ? x[i] : x;
						var z = isinstance ? x1["instance"]["reference"].ToString() : x1["reference"].ToString();
						var split = z.Split('/');
						if (split.Length <= 1) continue;
						var a1 = await store.LoadFhirResource(split[1], split[0]);
						if (a1 != null) retVal.Add(a1);
					}
				}
			}

			return retVal;
		}
	}
}