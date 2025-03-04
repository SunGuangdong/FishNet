﻿using FishNet.CodeGenerating.Helping.Extension;
using FishNet.CodeGenerating.ILCore;
using FishNet.Connection;
using FishNet.Serializing;
using MonoFN.Cecil;
using MonoFN.Cecil.Cil;
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace FishNet.CodeGenerating.Helping
{
    internal class ReaderHelper : CodegenBase
    {
        #region Reflection references.
        internal TypeReference PooledReader_TypeRef;
        internal TypeReference Reader_TypeRef;
        internal TypeReference NetworkConnection_TypeRef;
        internal MethodReference PooledReader_ReadNetworkBehaviour_MethodRef;
        private readonly Dictionary<string, MethodReference> _instancedReaderMethods = new Dictionary<string, MethodReference>();// (new TypeReferenceComparer());
        private readonly Dictionary<string, MethodReference> _staticReaderMethods = new Dictionary<string, MethodReference>();// (new TypeReferenceComparer());
        private HashSet<TypeReference> _autoPackedMethods = new HashSet<TypeReference>(new TypeReferenceComparer());
        private MethodReference Reader_ReadPackedWhole_MethodRef;
        internal MethodReference Reader_ReadDictionary_MethodRef;
        internal MethodReference Reader_ReadToCollection_MethodRef;
        #endregion

        #region Const.
        internal const string READ_PREFIX = "Read";
        /// <summary>
        /// Types to exclude from being scanned for auto serialization.
        /// </summary>
        public static System.Type[] EXCLUDED_AUTO_SERIALIZER_TYPES => WriterHelper.EXCLUDED_AUTO_SERIALIZER_TYPES;
        /// <summary>
        /// Types to exclude from being scanned for auto serialization.
        /// </summary>
        public static string[] EXCLUDED_ASSEMBLY_PREFIXES => WriterHelper.EXCLUDED_ASSEMBLY_PREFIXES;
        #endregion

        /// <summary>
        /// Imports references needed by this helper.
        /// </summary>
        /// <param name="moduleDef"></param>
        /// <returns></returns>
        public override bool ImportReferences()
        {
            PooledReader_TypeRef = base.ImportReference(typeof(PooledReader));
            Reader_TypeRef = base.ImportReference(typeof(Reader));
            NetworkConnection_TypeRef = base.ImportReference(typeof(NetworkConnection));

            Type pooledReaderType = typeof(PooledReader);

            foreach (MethodInfo methodInfo in pooledReaderType.GetMethods())
            {
                /* Special methods. */
                //ReadPackedWhole.
                if (methodInfo.Name == nameof(PooledReader.ReadPackedWhole))
                {
                    Reader_ReadPackedWhole_MethodRef = base.ImportReference(methodInfo);
                    continue;
                }
                //ReadToCollection.
                else if (methodInfo.Name == nameof(PooledReader.ReadArray))
                {
                    Reader_ReadToCollection_MethodRef = base.ImportReference(methodInfo);
                    continue;
                }
                //ReadDictionary.
                else if (methodInfo.Name == nameof(PooledReader.ReadDictionary))
                {
                    Reader_ReadDictionary_MethodRef = base.ImportReference(methodInfo);
                    continue;
                }

                else if (base.GetClass<GeneralHelper>().CodegenExclude(methodInfo))
                    continue;
                ////Generic methods are not supported.
                //else if (methodInfo.IsGenericMethod)
                //    continue;
                //Not long enough to be a write method.
                else if (methodInfo.Name.Length < READ_PREFIX.Length)
                    continue;
                //Method name doesn't start with writePrefix.
                else if (methodInfo.Name.Substring(0, READ_PREFIX.Length) != READ_PREFIX)
                    continue;
                ParameterInfo[] parameterInfos = methodInfo.GetParameters();
                //Can have at most one parameter for packing.
                if (parameterInfos.Length > 1)
                    continue;
                //If has one parameter make sure it's a packing type.
                bool autoPackMethod = false;
                if (parameterInfos.Length == 1)
                {
                    autoPackMethod = (parameterInfos[0].ParameterType == typeof(AutoPackType));
                    if (!autoPackMethod)
                        continue;
                }

                MethodReference methodRef = base.ImportReference(methodInfo);
                /* TypeReference for the return type
                 * of the read method. */
                TypeReference typeRef = base.ImportReference(methodRef.ReturnType);


                /* If here all checks pass. */
                AddReaderMethod(typeRef, methodRef, true, true);
                if (autoPackMethod)
                    _autoPackedMethods.Add(typeRef);
            }

            Type readerExtensionsType = typeof(ReaderExtensions);

            foreach (MethodInfo methodInfo in readerExtensionsType.GetMethods())
            {
                if (base.GetClass<GeneralHelper>().CodegenExclude(methodInfo))
                    continue;
                ////Generic methods are not supported.
                //if (methodInfo.IsGenericMethod)
                //    continue;
                //Not static.
                if (!methodInfo.IsStatic)
                    continue;
                //Not long enough to be a write method.
                if (methodInfo.Name.Length < READ_PREFIX.Length)
                    continue;
                //Method name doesn't start with writePrefix.
                if (methodInfo.Name.Substring(0, READ_PREFIX.Length) != READ_PREFIX)
                    continue;
                ParameterInfo[] parameterInfos = methodInfo.GetParameters();
                //Can have at most one parameter for packing.
                if (parameterInfos.Length > 2)
                    continue;
                //If has 2 parameters make sure it's a packing type.
                bool autoPackMethod = false;
                if (parameterInfos.Length == 2)
                {
                    autoPackMethod = (parameterInfos[1].ParameterType == typeof(AutoPackType));
                    if (!autoPackMethod)
                        continue;
                }

                MethodReference methodRef = base.ImportReference(methodInfo);
                /* TypeReference for the return type
                 * of the read method. */
                TypeReference typeRef = base.ImportReference(methodRef.ReturnType);                

                /* If here all checks pass. */
                AddReaderMethod(typeRef, methodRef, false, true);
            }


            return true;
        }


        /// <summary>
        /// Creates generic write delegates for all currently known write types.
        /// </summary>
        internal bool CreateGenericDelegates()
        {
            foreach (KeyValuePair<string, MethodReference> item in _staticReaderMethods)
                base.GetClass<GenericReaderHelper>().CreateReadDelegate(item.Value, true);
            //Only write instanced ones to fishnet assembly so they arent done redundantly for each asm.
            if (FishNetILPP.IsFishNetAssembly(base.Session))
            {
                foreach (KeyValuePair<string, MethodReference> item in _instancedReaderMethods)
                    base.GetClass<GenericReaderHelper>().CreateReadDelegate(item.Value, false);
            }
            return true;
        }


        /// <summary>
        /// Returns if typeRef has a deserializer.
        /// </summary>
        /// <param name="typeRef"></param>
        /// <param name="createMissing"></param>
        /// <returns></returns>
        internal bool HasDeserializer(TypeReference typeRef, bool createMissing)
        {
            bool result = (GetInstancedReadMethodReference(typeRef) != null) ||
                (GetStaticReadMethodReference(typeRef) != null);

            if (!result && createMissing)
            {
                if (!base.GetClass<GeneralHelper>().HasNonSerializableAttribute(typeRef.CachedResolve(base.Session)))
                {
                    MethodReference methodRef = base.GetClass<ReaderGenerator>().CreateReader(typeRef);
                    result = (methodRef != null);
                }
            }

            return result;
        }


        /// <summary>
        /// Returns if typeRef supports auto packing.
        /// </summary>
        /// <param name="typeRef"></param>
        /// <returns></returns>
        internal bool IsAutoPackedType(TypeReference typeRef)
        {
            return _autoPackedMethods.Contains(typeRef);
        }
        /// <summary>
        /// Creates a null check on the first argument and returns a null object if result indicates to do so.
        /// </summary>
        internal void CreateRetOnNull(ILProcessor processor, ParameterDefinition readerParameterDef, VariableDefinition resultVariableDef, bool useBool)
        {
            Instruction endIf = processor.Create(OpCodes.Nop);

            if (useBool)
                CreateReadBool(processor, readerParameterDef, resultVariableDef);
            else
                CreateReadPackedWhole(processor, readerParameterDef, resultVariableDef);

            //If (true or == -1) jmp to endIf. True is null.
            processor.Emit(OpCodes.Ldloc, resultVariableDef);
            if (useBool)
            {
                processor.Emit(OpCodes.Brfalse, endIf);
            }
            else
            {
                //-1
                processor.Emit(OpCodes.Ldc_I4_M1);
                processor.Emit(OpCodes.Bne_Un_S, endIf);
            }
            //Insert null.
            processor.Emit(OpCodes.Ldnull);
            //Exit method.
            processor.Emit(OpCodes.Ret);
            //End of if check.
            processor.Append(endIf);
        }

        /// <summary>
        /// Creates a call to WriteBoolean with value.
        /// </summary>
        /// <param name="processor"></param>
        /// <param name="writerParameterDef"></param>
        /// <param name="value"></param>
        internal void CreateReadBool(ILProcessor processor, ParameterDefinition readerParameterDef, VariableDefinition localBoolVariableDef)
        {
            MethodReference readBoolMethodRef = GetFavoredReadMethodReference(base.GetClass<GeneralHelper>().GetTypeReference(typeof(bool)), true);
            processor.Emit(OpCodes.Ldarg, readerParameterDef);
            processor.Emit(OpCodes.Callvirt, readBoolMethodRef);
            processor.Emit(OpCodes.Stloc, localBoolVariableDef);
        }

        /// <summary>
        /// Creates a call to WritePackWhole with value.
        /// </summary>
        /// <param name="processor"></param>
        /// <param name="value"></param>
        internal void CreateReadPackedWhole(ILProcessor processor, ParameterDefinition readerParameterDef, VariableDefinition resultVariableDef)
        {
            //Reader.
            processor.Emit(OpCodes.Ldarg, readerParameterDef);
            //Reader.ReadPackedWhole().
            processor.Emit(OpCodes.Callvirt, Reader_ReadPackedWhole_MethodRef);
            processor.Emit(OpCodes.Conv_I4);
            processor.Emit(OpCodes.Stloc, resultVariableDef);
        }


        #region GetReaderMethodReference.
        /// <summary>
        /// Returns the MethodReference for typeRef.
        /// </summary>
        /// <param name="typeRef"></param>
        /// <returns></returns>
        internal MethodReference GetInstancedReadMethodReference(TypeReference typeRef)
        {
            string fullName = base.GetClass<GeneralHelper>().RemoveGenericBrackets(typeRef.FullName);
            _instancedReaderMethods.TryGetValue(fullName, out MethodReference methodRef);
            return methodRef;
        }
        /// <summary>
        /// Returns the MethodReference for typeRef.
        /// </summary>
        /// <param name="typeRef"></param>
        /// <returns></returns>
        internal MethodReference GetStaticReadMethodReference(TypeReference typeRef)
        {
            string fullName = base.GetClass<GeneralHelper>().RemoveGenericBrackets(typeRef.FullName);
            _staticReaderMethods.TryGetValue(fullName, out MethodReference methodRef);
            return methodRef;
        }
        /// <summary>
        /// Returns the MethodReference for typeRef favoring instanced or static. Returns null if not found.
        /// </summary>
        /// <param name="typeRef"></param>
        /// <param name="favorInstanced"></param>
        /// <returns></returns>
        internal MethodReference GetFavoredReadMethodReference(TypeReference typeRef, bool favorInstanced)
        {
            MethodReference result;
            if (favorInstanced)
            {
                result = GetInstancedReadMethodReference(typeRef);
                if (result == null)
                    result = GetStaticReadMethodReference(typeRef);
            }
            else
            {
                result = GetStaticReadMethodReference(typeRef);
                if (result == null)
                    result = GetInstancedReadMethodReference(typeRef);
            }

            return result;
        }
        /// <summary>
        /// Returns the MethodReference for typeRef favoring instanced or static.
        /// </summary>
        /// <param name="typeRef"></param>
        /// <param name="favorInstanced"></param>
        /// <returns></returns>
        internal MethodReference GetOrCreateFavoredReadMethodReference(TypeReference typeRef, bool favorInstanced)
        {
            //Try to get existing writer, if not present make one.
            MethodReference readMethodRef = GetFavoredReadMethodReference(typeRef, favorInstanced);
            if (readMethodRef == null)
                readMethodRef = base.GetClass<ReaderGenerator>().CreateReader(typeRef);

            //If still null then return could not be generated.
            if (readMethodRef == null)
            {
                base.LogError($"Could not create deserializer for {typeRef.FullName}.");
            }
            //Otherwise, check if generic and create writes for generic pararameters.
            else if (typeRef.IsGenericInstance)
            {
                GenericInstanceType git = (GenericInstanceType)typeRef;
                foreach (TypeReference item in git.GenericArguments)
                {
                    MethodReference result = GetOrCreateFavoredReadMethodReference(item, favorInstanced);
                    if (result == null)
                    {
                        base.LogError($"Could not create deserializer for {item.FullName}.");
                        return null;
                    }
                }
            }

            return readMethodRef;
        }
        #endregion

        /// <summary>
        /// Adds typeRef, methodDef to instanced or readerMethods.
        /// </summary>
        /// <param name="typeRef"></param>
        /// <param name="methodRef"></param>
        /// <param name="useAdd"></param>
        internal void AddReaderMethod(TypeReference typeRef, MethodReference methodRef, bool instanced, bool useAdd)
        {
            string fullName = base.GetClass<GeneralHelper>().RemoveGenericBrackets(typeRef.FullName);
            Dictionary<string, MethodReference> dict = (instanced) ?
                _instancedReaderMethods : _staticReaderMethods;

            if (useAdd)
                dict.Add(fullName, methodRef);
            else
                dict[fullName] = methodRef;
        }

        /// <summary>
        /// Removes typeRef from static/instanced reader methods.
        /// </summary>
        internal void RemoveReaderMethod(TypeReference typeRef, bool instanced)
        {
            string fullName = base.GetClass<GeneralHelper>().RemoveGenericBrackets(typeRef.FullName);
            Dictionary<string, MethodReference> dict = (instanced) ?
                _instancedReaderMethods : _staticReaderMethods;

            dict.Remove(fullName);
        }

        /// <summary>
        /// Creates read instructions returning instructions and outputing variable of read result.
        /// </summary>
        /// <param name="processor"></param>
        /// <param name="methodDef"></param>
        /// <param name="readerParameterDef"></param>
        /// <param name="readTypeRef"></param>
        /// <param name="diagnostics"></param>
        /// <returns></returns>
        internal List<Instruction> CreateRead(MethodDefinition methodDef, ParameterDefinition readerParameterDef, TypeReference readTypeRef, out VariableDefinition createdVariableDef)
        {
            ILProcessor processor = methodDef.Body.GetILProcessor();
            List<Instruction> insts = new List<Instruction>();
            MethodReference readMr = GetFavoredReadMethodReference(readTypeRef, true);
            if (readMr != null)
            {
                //Make a local variable. 
                createdVariableDef = base.GetClass<GeneralHelper>().CreateVariable(methodDef, readTypeRef);
                //pooledReader.ReadBool();
                insts.Add(processor.Create(OpCodes.Ldarg, readerParameterDef));
                //If an auto pack method then insert default value.
                if (_autoPackedMethods.Contains(readTypeRef))
                {
                    AutoPackType packType = base.GetClass<GeneralHelper>().GetDefaultAutoPackType(readTypeRef);
                    insts.Add(processor.Create(OpCodes.Ldc_I4, (int)packType));
                }


                TypeReference valueTr = readTypeRef;
                /* If generic then find write class for
                 * data type. Currently we only support one generic
                 * for this. */
                if (valueTr.IsGenericInstance)
                {
                    GenericInstanceType git = (GenericInstanceType)valueTr;
                    TypeReference genericTr = git.GenericArguments[0];
                    readMr = readMr.GetMethodReference(base.Session, genericTr);
                }

                insts.Add(processor.Create(OpCodes.Call, readMr));
                //Store into local variable.
                insts.Add(processor.Create(OpCodes.Stloc, createdVariableDef));
                return insts;
            }
            else
            {
                base.LogError("Reader not found for " + readTypeRef.ToString());
                createdVariableDef = null;
                return null;
            }
        }



        /// <summary>
        /// Creates a read for fieldRef and populates it into a created variable of class or struct type.
        /// </summary> 
        internal bool CreateReadIntoClassOrStruct(MethodDefinition readerMd, ParameterDefinition readerPd, MethodReference readMr, VariableDefinition objectVd, FieldReference valueFr)
        {
            if (readMr != null)
            {
                ILProcessor processor = readerMd.Body.GetILProcessor();
                /* How to load object instance. If it's a structure
                 * then it must be loaded by address. Otherwise if
                 * class Ldloc can be used. */
                OpCode loadOpCode = (objectVd.VariableType.IsValueType) ?
                    OpCodes.Ldloca : OpCodes.Ldloc;

                /* If generic then find write class for
                 * data type. Currently we only support one generic
                 * for this. */
                if (valueFr.FieldType.IsGenericInstance)
                {
                    GenericInstanceType git = (GenericInstanceType)valueFr.FieldType;
                    TypeReference genericTr = git.GenericArguments[0];
                    readMr = readMr.GetMethodReference(base.Session, genericTr);
                }

                processor.Emit(loadOpCode, objectVd);
                //reader.
                processor.Emit(OpCodes.Ldarg, readerPd);
                if (IsAutoPackedType(valueFr.FieldType))
                {
                    AutoPackType packType = base.GetClass<GeneralHelper>().GetDefaultAutoPackType(valueFr.FieldType);
                    processor.Emit(OpCodes.Ldc_I4, (int)packType);
                }
                //reader.ReadXXXX().
                processor.Emit(OpCodes.Call, readMr);
                //obj.Field = result / reader.ReadXXXX().
                processor.Emit(OpCodes.Stfld, valueFr);

                return true;
            }
            else
            {
                base.LogError($"Reader not found for {valueFr.FullName}.");
                return false;
            }
        }


        /// <summary>
        /// Creates a read for fieldRef and populates it into a created variable of class or struct type.
        /// </summary>
        internal bool CreateReadIntoClassOrStruct(MethodDefinition methodDef, ParameterDefinition readerPd, MethodReference readMr, VariableDefinition objectVariableDef, MethodReference setMr, TypeReference readTr)
        {
            if (readMr != null)
            {
                ILProcessor processor = methodDef.Body.GetILProcessor();

                /* How to load object instance. If it's a structure
                 * then it must be loaded by address. Otherwise if
                 * class Ldloc can be used. */
                OpCode loadOpCode = (objectVariableDef.VariableType.IsValueType) ?
                    OpCodes.Ldloca : OpCodes.Ldloc;

                /* If generic then find write class for
                 * data type. Currently we only support one generic
                 * for this. */ 
                if (readTr.IsGenericInstance)
                {
                    GenericInstanceType git = (GenericInstanceType)readTr;
                    TypeReference genericTr = git.GenericArguments[0];
                    readMr = readMr.GetMethodReference(base.Session, genericTr);
                }

                processor.Emit(loadOpCode, objectVariableDef);
                //reader.
                processor.Emit(OpCodes.Ldarg, readerPd);
                if (IsAutoPackedType(readTr))
                {
                    AutoPackType packType = base.GetClass<GeneralHelper>().GetDefaultAutoPackType(readTr);
                    processor.Emit(OpCodes.Ldc_I4, (int)packType);
                }
                //reader.ReadXXXX().
                processor.Emit(OpCodes.Call, readMr);
                //obj.Property = result / reader.ReadXXXX().
                processor.Emit(OpCodes.Call, setMr);

                return true;
            }
            else
            {
                base.LogError($"Reader not found for {readTr.FullName}.");
                return false;
            }
        }
    }
}