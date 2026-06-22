using MultiProject.Models;

namespace MultiProject.Abstractions;

public interface IService
{
    UserDto LoadUser(System.Guid id);
}
