using System;

namespace FarNet.PCloud.Exceptions
{
    /// <summary>
    /// TODO
    /// </summary>
    public class RemoteFileExistsException: IOException
    {
        /// <summary>
        /// TODO
        /// </summary>
        public RemoteFileExistsException()
        : base()
        {
        }

        /// <summary>
        /// TODO
        /// </summary>
        /// <param name="message"></param>
        public RemoteFileExistsException(string message) 
        : base(message)
        {
        }

        /// <summary>
        /// TODO
        /// </summary>
        /// <param name="message"></param>
        /// <param name="e"></param>
        public RemoteFileExistsException(string message, Exception e)
        : base(message, e) 
        {
        }
    }
}
