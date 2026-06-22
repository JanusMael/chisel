using MultiProject.Abstractions;
using MultiProject.Models;

namespace MultiProject.Impl;

public class ServiceImpl : IService
{
    public UserDto LoadUser(System.Guid id) => new UserDto(id, "user@example.com");
}
