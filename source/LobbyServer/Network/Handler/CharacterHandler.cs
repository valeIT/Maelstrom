﻿using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using LobbyServer.Manager;
using LobbyServer.Network.Message;
using SaintCoinach.Xiv;
using Shared.Database;
using Shared.Database.Datacentre;
using Shared.Game;
using Shared.Network;
using Shared.SqPack;

namespace LobbyServer.Network.Handler
{
    public class CharacterHandler
    {
        [SubPacketHandler(SubPacketClientOpcode.ClientCharacterList, SubPacketHandlerFlags.RequiresEncryption)]
        public static void HandleClientSessionRequest(LobbySession session, ClientCharacterList characterList)
        {
            session.Sequence = characterList.Sequence;

            if (characterList.ServiceAccount >= session.ServiceAccounts.Count)
                return;

            // must be sent before characrer list otherwise crash
            SendRealmList(session);

            session.ServiceAccount = session.ServiceAccounts[characterList.ServiceAccount];
            SendCharacterList(session);
            
            // SendRetainerList();
        }

        private static void SendRealmList(LobbySession session)
        {
            ReadOnlyCollection<RealmInfo> realmInfo = AssetManager.RealmInfoStore;

            ServerRealmList realmList = new ServerRealmList { Sequence = session.Sequence };
            for (ushort i = 0; i < realmInfo.Count; i++)
            {
                // client expects no more than 6 realms per chunk
                if (i % ServerRealmList.MaxRealmsPerPacket == 0)
                {
                    // flush previous chunk
                    if (i != 0)
                    {
                        session.Send(realmList);
                        realmList.Realms.Clear();
                    }

                    realmList.Offset = i;
                    realmList.Final  = (ushort)(realmInfo.Count - i < ServerRealmList.MaxRealmsPerPacket ? 1 : 0);
                }

                RealmInfo realm = realmInfo[i];
                realmList.Realms.Add(new ServerRealmList.RealmInfo
                {
                    Id       = realm.Id,
                    Position = i,
                    Name     = realm.Name,
                    Flags    = realm.Flags
                });

                // flush final chunk
                if (i == realmInfo.Count - 1)
                    session.Send(realmList);
            }
        }

        private static void SendCharacterList(LobbySession session)
        {
            session.NewEvent(new DatabaseGenericEvent<List<CharacterInfo>>(DatabaseManager.DataCentre.GetCharacters(session.ServiceAccount.Id), characters =>
            {
                session.Characters = characters;

                ServerCharacterList characterList = new ServerCharacterList
                {
                    VeteranRank               = 0,
                    DaysTillNextVeteranRank   = 0u,
                    DaysSubscribed            = 0u,
                    SubscriptionDaysRemaining = 0u,
                    RealmCharacterLimit       = session.ServiceAccount.RealmCharacterLimit,
                    AccountCharacterLimit     = session.ServiceAccount.AccountCharacterLimit,
                    Expansion                 = session.ServiceAccount.Expansion,
                    Offset                    = 1
                };

                if (session.Characters.Count == 0)
                {
                    session.Send(characterList);
                    return;
                }

                for (int i = 0; i < session.Characters.Count; i++)
                {
                    // client expects no more than 2 characters per chunk
                    if (i % ServerCharacterList.MaxCharactersPerPacket == 0)
                    {
                        // flush previous chunk
                        if (i != 0)
                        {
                            session.Send(characterList);
                            session.FlushPacketQueue();
                            characterList.Characters.Clear();
                        }

                        // weird...
                        characterList.Offset = (byte)(session.Characters.Count - i <= ServerCharacterList.MaxCharactersPerPacket ? i * 2 + 1 : i * 2);
                    }

                    RealmInfo realmInfo = AssetManager.GetRealmInfo(session.Characters[i].RealmId);
                    if (realmInfo == null)
                        continue;

                    characterList.Characters.Add(((byte)i, realmInfo.Name, session.Characters[i]));
                
                    // flush final chunk
                    if (i == session.Characters.Count - 1)
                    {
                        session.Send(characterList);
                        session.FlushPacketQueue();
                    }
                }
            }));
        }

        [SubPacketHandler(SubPacketClientOpcode.ClientCharacterCreate, SubPacketHandlerFlags.RequiresEncryption | SubPacketHandlerFlags.RequiresAccount)]
        public static void HandleCharacterCreate(LobbySession session, ClientCharacterCreate characterCreate)
        {
            session.Sequence = characterCreate.Sequence;
            switch (characterCreate.Type)
            {
                // verify
                case 1:
                {
                    RealmInfo realmInfo = AssetManager.GetRealmInfo(characterCreate.RealmId);
                    if (realmInfo == null)
                        return;

                    session.NewEvent(new DatabaseGenericEvent<bool>(DatabaseManager.DataCentre.IsCharacterNameAvailable(characterCreate.Name), available =>
                    {
                        if (!CharacterInfo.VerifyName(characterCreate.Name) || !available)
                        {
                            session.SendError(3035, 13004);
                            return;
                        }

                        if (session.Characters?.Count >= session.ServiceAccount.AccountCharacterLimit)
                        {
                            session.SendError(3035, 13203);
                            return;
                        }

                        if (session.Characters?.Count(c => c.RealmId == realmInfo.Id) >= session.ServiceAccount.RealmCharacterLimit)
                        {
                            session.SendError(3035, 13204);
                            return;
                        }

                        session.CharacterCreate = (realmInfo.Id, characterCreate.Name);
                        session.Send(new ServerCharacterCreate
                        {
                            Sequence = session.Sequence,
                            Type     = 1,
                            Name     = characterCreate.Name,
                            Realm    = realmInfo.Name
                        });
                    }));
                    break;
                }
                // create
                case 2:
                {
                    if (session.CharacterCreate.Name == string.Empty)
                        return;

                    CharacterInfo characterInfo;
                    try
                    {
                        characterInfo = new CharacterInfo(session.ServiceAccount.Id, session.CharacterCreate.RealmId, session.CharacterCreate.Name, characterCreate.Json);
                    }
                    catch
                    {
                        // should only occur if JSON data is tampered with
                        return;
                    }

                    if (!characterInfo.Verify())
                        return;

                    ClassJob entry = GameTableManager.ClassJobs[characterInfo.ClassJobId];
                    Debug.Assert(entry.ClassId >= 0);

                    characterInfo.AddClassInfo((byte)entry.ClassId);

                    if (!AssetManager.GetCharacterSpawn(entry.CityState, out WorldPosition spawnPosition))
                        return;

                    characterInfo.Finalise(AssetManager.GetNewCharacterId(), spawnPosition);

                    session.NewEvent(new DatabaseEvent(characterInfo.SaveToDatabase(), () =>
                    {
                        RealmInfo realmInfo = AssetManager.GetRealmInfo(session.CharacterCreate.RealmId);
                        Debug.Assert(realmInfo != null);

                        session.Send(new ServerCharacterCreate
                        {
                            Sequence    = session.Sequence,
                            Type        = 2,
                            Name        = characterCreate.Name,
                            Realm       = realmInfo.Name,
                            CharacterId = characterInfo.Id
                        });

                        session.Characters.Add(characterInfo);
                        session.CharacterCreate = (0, string.Empty);
                    }, exception =>
                    {
                        // should only occur if name was claimed in the time between verification and creation
                        session.SendError(3035, 13208);
                    }));
                    break;
                }
            }
        }

        [SubPacketHandler(SubPacketClientOpcode.ClientCharacterDelete, SubPacketHandlerFlags.RequiresEncryption | SubPacketHandlerFlags.RequiresAccount)]
        public static void HandleCharacterDelete(LobbySession session, SubPacket subPacket)
        {
            // TODO
        }

        [SubPacketHandler(SubPacketClientOpcode.ClientEnterWorld, SubPacketHandlerFlags.RequiresEncryption | SubPacketHandlerFlags.RequiresAccount)]
        public static void HandleClientEnterWorld(LobbySession session, ClientEnterWorld enterWorld)
        {
            session.Sequence = enterWorld.Sequence;

            CharacterInfo character = session.Characters.SingleOrDefault(c => c.Id == enterWorld.CharacterId);
            if (character == null)
                return;

            RealmInfo realmInfo = AssetManager.GetRealmInfo(character.RealmId);
            if (realmInfo == null)
                return;

            session.NewEvent(new DatabaseEvent(DatabaseManager.DataCentre.CreateCharacterSession(character.Id, session.Remote.ToString()), () =>
            {
                session.Send(new ServerEnterWorld
                {
                    Sequence    = session.Sequence,
                    ActorId     = character.ActorId,
                    CharacterId = enterWorld.CharacterId,
                    Token       = "",
                    Host        = realmInfo.Host,
                    Port        = realmInfo.Port
                });
            }));
        }
    }
}
