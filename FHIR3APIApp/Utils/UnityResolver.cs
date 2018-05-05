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
using System.Web.Http.Dependencies;
using Unity;
using Unity.Exceptions;

namespace FHIR4APIApp.Utils
{
	public class UnityResolver : IDependencyResolver
	{
		protected IUnityContainer Container;

		public UnityResolver(IUnityContainer container)
		{
			Container = container ?? throw new ArgumentNullException(nameof(container));
		}

		public object GetService(Type serviceType)
		{
			try
			{
				return Container.Resolve(serviceType);
			}
			catch (ResolutionFailedException)
			{
				return null;
			}
		}

		public IEnumerable<object> GetServices(Type serviceType)
		{
			try
			{
				return Container.ResolveAll(serviceType);
			}
			catch (ResolutionFailedException)
			{
				return new List<object>();
			}
		}

		public IDependencyScope BeginScope()
		{
			var child = Container.CreateChildContainer();
			return new UnityResolver(child);
		}

		public void Dispose()
		{
			Container.Dispose();
		}
	}
}