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
using System.Reflection;
using System.Text;
using FHIR4APIApp.Providers;

namespace FHIR4APIApp.Utils
{
	public class FhirParmMapper
	{
		private static volatile FhirParmMapper instance;
		private static readonly object SyncRoot = new object();
		private readonly Dictionary<string, string> pmap = new Dictionary<string, string>();

		private FhirParmMapper()
		{
			var assembly = Assembly.GetExecutingAssembly();
			const string resourceName = "FHIR4APIApp.FHIRParameterMappings.txt";
			using (var stream = assembly.GetManifestResourceStream(resourceName))
			using (var reader = new StreamReader(stream ?? throw new InvalidOperationException()))
			{
				string s;
				while ((s = reader.ReadLine()) != null)
					if (!s.StartsWith("#"))
					{
						var split = s.IndexOf('=');
						if (split <= -1) continue;
						var name = s.Substring(0, split);
						var value = s.Substring(split + 1);
						pmap.Add(name, value);
					}
			}
		}

		public static FhirParmMapper Instance
		{
			get
			{
				if (instance == null)
					lock (SyncRoot)
					{
						if (instance == null)
							instance = new FhirParmMapper();
					}

				return instance;
			}
		}

		public string GenerateQuery(IFhirStore store, string resourceType, NameValueCollection parms)
		{
			var where = new StringBuilder();
			//Select statement for Resource
			var select = new StringBuilder();
			select.Append(store.SelectAllQuery);
			foreach (string key in parms)
			{
				var value = parms[key];
				pmap.TryGetValue(resourceType + "." + key, out var parmdef);
				if (parmdef == null) continue;
				//TODO Handle Setting up Parm Type and process value for prefix and modifiers
				//Add JOINS to select
				pmap.TryGetValue(resourceType + "." + key + ".join", out var join);
				if (join != null)
					if (!select.ToString().Contains(join))
						select.Append(" " + join);
				//Add Where clauses/bind values
				pmap.TryGetValue(resourceType + "." + key + ".default", out var querypiece);
				if (querypiece == null) continue;
				where.Append(where.Length == 0 ? " WHERE (" : " and (");
				//Handle bind values single or multiple
				var vals = value.Split(',');
				foreach (var s in vals)
				{
					var currentpiece = querypiece;
					var t = s.Split('|');
					var x = 0;
					currentpiece = t.Aggregate(currentpiece, (current, u) => current.Replace("~v" + x++ + "~", u));
					where.Append("(" + currentpiece + ") OR ");
				}

				where.Remove(where.Length - 3, 3);
				where.Append(")");
			}

			return select + where.ToString();
		}
	}
}