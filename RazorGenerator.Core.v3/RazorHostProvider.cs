using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.ComponentModel.Composition;

namespace RazorGenerator.Core
{
    [Export(typeof(IHostProvider))]
    public class RazorHostProvider : IHostProvider
    {
        public IRazorHost GetRazorHost(string projectRelativePath, 
                                       string fullPath, 
                                       IRazorCodeTransformer codeTransformer, 
                                       CodeDomProvider codeDomProvider, 
                                       IDictionary<string, string> directives)
        {
            return new RazorHost(projectRelativePath, fullPath, codeTransformer, codeDomProvider, directives);
        }
    }
}
