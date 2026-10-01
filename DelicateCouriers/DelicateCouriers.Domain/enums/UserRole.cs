using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DelicateCouriers.Domain.enums
{
    // <summary>
    /// Available user roles in the system
    /// </summary>
    public enum UserRole
    {
        /// <summary>
        /// Basic user with limited access
        /// </summary>
        User,

        /// <summary>
        /// Administrator with full tenant access
        /// </summary>
        Admin,

        /// <summary>
        /// Super administrator with full platform access (Delicate Couriers team)
        /// </summary>
        SuperAdmin
    }
}
