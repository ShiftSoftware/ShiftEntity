using ShiftSoftware.ShiftEntity.Model.HashId;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ShiftSoftware.ShiftEntity.Web;

public class HashIdOptions
{
    public HashIdOptions RegisterHashId(bool acceptUnencodedIds)
    {
        HashId.RegisterHashId(acceptUnencodedIds);
        return this;
    }

    public HashIdOptions RegisterUserIdsHasher(string salt = "", int minHashLength = 0, string? alphabet = null)
    {
        HashId.UserIdsSalt = salt;
        HashId.UserIdsMinHashLength = minHashLength;
        HashId.UserIdsAlphabet = alphabet;

        HashId.UserIdsHashEnabled = true;

        return this;
    }
}
