﻿/* 
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
using System.Linq;
using System.Web.Http;
using System.Web.Http.Controllers;
using Microsoft.Azure;

namespace FHIR3APIApp.Security
{
	public class FhirAuthorize : AuthorizeAttribute
	{
		protected override bool IsAuthorized(HttpActionContext actionContext)
		{
			//No Security for Capability Statement
			if (actionContext.Request.RequestUri.AbsolutePath.EndsWith("metadata")) return true;
			//Is HeaderSecret required and/or present
			var hs = CloudConfigurationManager.GetSetting("HeaderSecret");
			if (!string.IsNullOrEmpty(hs))
			{
				string rh = null;
				var rhc = actionContext.Request.Headers.GetValues("fhirserversecret");
				if (rhc != null) rh = rhc.First();
				if (string.IsNullOrEmpty(rh) || !rh.Equals(hs)) return false;
			}

			//Is Authorization enabled
			var enableauth = Convert.ToBoolean(CloudConfigurationManager.GetSetting("EnableAuth"));
			return !enableauth || base.IsAuthorized(actionContext);
		}
	}
}