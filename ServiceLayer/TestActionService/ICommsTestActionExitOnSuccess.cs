﻿using System;
using GenericServices;
using ServiceLayer.TestActionService.Concrete;

namespace ServiceLayer.TestActionService
{
    public interface ICommsTestActionExitOnSuccess : IActionDefn<CommsTestActionData>, IDisposable
    {

    }
}
