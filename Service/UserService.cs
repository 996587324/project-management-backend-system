using AutoMapper;
using Interface;
using Model.Dto;
using Model.Entitys;
using Model.Other;
using Model.User;
using SqlSugar;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Service
{
    public class UserService : IUserService
    {
        private readonly IMapper _mapper;
        private SqlSugarScope _db { get; set; }
        public UserService(IMapper mapper, SqlSugarScope db)
        {
            _mapper = mapper;
            _db = db;
        }
        public UserRes GetUser(string userName, string password)
        {
            _db.GetConnection("calmadmin").CodeFirst.SetStringDefaultLength(200)
                .InitTables(typeof(SysUserInfo), typeof(SysRole));

            var account = _db.GetConnection("calmadmin").Queryable<SysUserInfo>()
                .Where(u => u.UserName == userName && u.Password == password && u.Enabled)
                .First();
            if (account != null)
            {
                return new UserRes
                {
                    Id = long.TryParse(account.UserId, out var id) ? id : 0,
                    Name = account.UserName,
                    Password = account.Password,
                    UserType = 0,
                    IsEnable = account.Enabled,
                    Description = account.NickName,
                    CreateDate = account.CreateTime
                };
            }

            if (!_db.GetConnection("RoleMenuDb").DbMaintenance.IsAnyTable("Users"))
            {
                _db.GetConnection("RoleMenuDb").CodeFirst.SetStringDefaultLength(200).InitTables(typeof(Users));
                _db.GetConnection("RoleMenuDb").Insertable<Users>(new Users
                { Name = "admin",Password = "123456"
                ,ModifyDate = DateTime.Now,Description = "管理员",IsEnable = true,UserType = 0,NickName = "李金洋"}).ExecuteCommand();
            }

            var user = _db.GetConnection("RoleMenuDb").Queryable<Users>().Where(u => u.Name == userName && u.Password == password).First();
            if (user != null)
            {
                return _mapper.Map<UserRes>(user);
            }
            return new UserRes();
        }
        public bool Add(UserAdd input, long userId)
        {
            Users info = _mapper.Map<Users>(input);
            info.CreateUserId = userId;
            info.CreateDate = DateTime.Now;
            info.UserType = 1;
            info.IsDeleted = 0;
            return _db.GetConnection("RoleMenuDb").Insertable(info).ExecuteCommand() > 0;
        }

        public bool Edit(UserEdit input, long userId)
        {
            var info = _db.GetConnection("RoleMenuDb").Queryable<Users>().First(p => p.Id == input.Id);
            _mapper.Map(input, info);
            info.ModifyUserId = userId;
            info.ModifyDate = DateTime.Now;
            return _db.GetConnection("RoleMenuDb").Updateable(info).ExecuteCommand() > 0;
        }

        public bool Del(long id)
        {
            var info = _db.GetConnection("RoleMenuDb").Queryable<Users>().First(p => p.Id == id);
            return _db.GetConnection("RoleMenuDb").Deleteable(info).ExecuteCommand() > 0;
        }

        public bool BatchDel(string ids)
        {
            return _db.GetConnection("RoleMenuDb").Ado.ExecuteCommand($"DELETE Users WHERE Id IN({ids})") > 0;
        }

     
        private string GetRolesByUserId(long uid)
        {
            return _db.GetConnection("RoleMenuDb").Ado.SqlQuery<string>($@"SELECT STUFF((SELECT ','+R.Name FROM dbo.Role R
                    LEFT JOIN dbo.UserRoleRelation UR ON R.Id=UR.RoleId
                    WHERE UR.UserId={uid} FOR XML PATH('')),1,1,'') RoleNames")[0];
        }
        public UserRes GetUsersById(long id)
        {
            var info = _db.GetConnection("RoleMenuDb").Queryable<Users>().First(p => p.Id == id);
            return _mapper.Map<UserRes>(info);
        }
       
        public bool EditNickNameOrPassword(long userId, string nickName, string password)
        {
            var info = _db.GetConnection("RoleMenuDb").Queryable<Users>().Where(p => p.Id == userId).First();
            if (info != null)
            {
                if (!string.IsNullOrEmpty(nickName))
                {
                    info.NickName = nickName;
                }
                if (!string.IsNullOrEmpty(password))
                {
                    info.Password = password;
                }
            }
            return _db.GetConnection("RoleMenuDb").Updateable(info).ExecuteCommand() > 0;
        }
    }
}
