namespace DelicateCouriers.ApiService.Features.SuperAdmin.DTOs
{
    public class SuperAdminDTO
    {

        // DTOs
        public class UpdateRoleRequest
        {
            public string Role { get; set; } = string.Empty;
        }

        public class UpdateStatusRequest
        {
            public bool IsActive { get; set; }
        }

        public class ResetPasswordRequest
        {
            public string NewPassword { get; set; } = string.Empty;
        }
    }
}
