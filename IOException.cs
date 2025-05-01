using System;

namespace FarNet.PCloud.Exceptions
{
    /// <summary>
    /// TODO
    /// </summary>
    public class IOException: Exception
    {
        /// <summary>
        /// TODO
        /// </summary>
        public IOException()
        : base()
        {
        }

        /// <summary>
        /// TODO
        /// </summary>
        /// <param name="message"></param>
        public IOException(string message) 
        : base(message)
        {
        }

        /// <summary>
        /// TODO
        /// </summary>
        /// <param name="message"></param>
        /// <param name="e"></param>
        public IOException(string message, Exception e)
        : base(message, e) 
        {
        }
    }
}
