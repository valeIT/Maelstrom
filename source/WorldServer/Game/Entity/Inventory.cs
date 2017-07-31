﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Shared.Game.Enum;
using Shared.SqPack;
using Shared.SqPack.GameTable;
using WorldServer.Manager;
using WorldServer.Network;
using WorldServer.Network.Message;

namespace WorldServer.Game.Entity
{
    public partial class Inventory
    {
        private Player owner;
        private readonly Dictionary<ContainerType, Container> containers = new Dictionary<ContainerType, Container>();

        // TODO: load from database
        public Inventory(Player player)
        {
            Initialise(owner);
        }

        /// <summary>
        /// initialise an inventory for a new character with starting gear for class and race
        /// </summary>
        public Inventory(Player owner, Race raceId, Sex sexId, ClassJob classJobId)
        {
            Initialise(owner);
            
            if (!GameTableManager.Races.TryGetValue((uint)raceId, ExdLanguage.En, out RaceEntry raceTemplate))
                throw new ArgumentException($"Invalid race id {raceId}!");
                
            if (!GameTableManager.ClassJobs.TryGetValue((uint)classJobId, ExdLanguage.En, out ClassJobEntry classJobTemplate))
                throw new ArgumentException($"Invalid classJob id {classJobId}!");

            // default weapon and gear
            EquipItem((uint)classJobTemplate.WeaponItemId);
            EquipItem((uint)(sexId == Sex.Male ? raceTemplate.MaleBodyItemId : raceTemplate.FemaleBodyItemId));
            EquipItem((uint)(sexId == Sex.Male ? raceTemplate.MaleHandsItemId : raceTemplate.FemaleHandsItemId));
            EquipItem((uint)(sexId == Sex.Male ? raceTemplate.MaleLegsItemId : raceTemplate.FemaleLegsItemId));
            EquipItem((uint)(sexId == Sex.Male ? raceTemplate.MaleFeetItemId : raceTemplate.FemaleFeetItemId));
        }

        private void Initialise(Player player)
        {
            owner = player;
            foreach (ContainerType containerType in Enum.GetValues(typeof(ContainerType)))
            {
                if (containerType == ContainerType.None)
                    continue;
                
                ushort capacity = AssetManager.ContainerCapacities[containerType];
                containers.Add(containerType, new Container(containerType, capacity));
            }
        }

        private Container GetContainer(ContainerType containerType)
        {
            if (containerType == ContainerType.None)
                return null;
            
            Debug.Assert(containers.ContainsKey(containerType));
            return containers[containerType];
        }
        
        /// <summary>
        /// Get visible equipped weapons (slots 0-1).
        /// </summary>
        public (ulong MainHand, ulong OffHand) GetWeaponDisplayIds()
        {
            Container container = GetContainer(ContainerType.Equipped);

            Item mainHand = container.GetItem((ushort)ContainerEquippedSlot.MainHand);
            Item offhand  = container.GetItem((ushort)ContainerEquippedSlot.OffHand);

            // some main hands have a secondary model (eg: bow and quiver)
            return (mainHand?.Template.ModelPrimary ?? 0ul, offhand?.Template.ModelSecondary ?? (mainHand?.Template.ModelSecondary ?? 0ul));
        }

        /// <summary>
        /// Get visible equipped items excluding weapons (slots 2-4 and 6-12).
        /// </summary>
        public IEnumerable<uint> GetVisibleItemDisplayIds()
        {
            Container container = GetContainer(ContainerType.Equipped);
            for (ContainerEquippedSlot i = ContainerEquippedSlot.Head; i <= ContainerEquippedSlot.LeftRing; i++)
                if (i != ContainerEquippedSlot.Waist)
                    yield return (uint)(container.GetItem((ushort)i)?.Template.ModelPrimary ?? 0u);
        }

        private void AddItem(Item item, Container container, ushort slot, bool update = false)
        {
            Debug.Assert(item != null);
            Debug.Assert(container != null);

            if (container.ContainerType == ContainerType.Equipped)
                EquipItem(item, slot);
            else
                container.AddItem(item, slot, update);
        }

        private void RemoveItem(Item item, Container container)
        {
            Debug.Assert(item != null);
            Debug.Assert(container != null);

            if (container.ContainerType == ContainerType.Equipped)
                UnEquipItem(item);
            else
                container.RemoveItem(item);
        }

        /// <summary>
        /// Create a new item instance in the first valid inventory slot or stack.
        /// </summary>
        public void NewItem(uint itemId, uint count = 1u)
        {
            if (!GameTableManager.Items.TryGetValue(itemId, ExdLanguage.En, out ItemEntry template))
                throw new ArgumentException($"Invalid item id {itemId}!");

            uint countLeft = count;
            for (ContainerType containerType = ContainerType.Inventory0; containerType <= ContainerType.Inventory3; containerType++)
            {
                if (countLeft == 0)
                    break;

                Container container = GetContainer(containerType);
                if (template.MaxStackCount > 1)
                {
                    // update current item stacks before creating any new ones
                    using (IEnumerator<(ushort Slot, Item Item)> enumerator = container.GetItems(template.Index).GetEnumerator())
                    {
                        while (countLeft > 0 && enumerator.MoveNext())
                        {
                            uint stackChange = Math.Min(template.MaxStackCount - enumerator.Current.Item.StackSize, countLeft);
                            if (stackChange == 0)
                                continue;
                            
                            enumerator.Current.Item.UpdateStackSize(stackChange);
                            countLeft -= stackChange;
                        }
                    }
                }
                
                // create new items or stacks for remaining count
                for (ushort slot = container.GetFirstAvailableSlot(); countLeft > 0 && slot != ushort.MaxValue; slot = container.GetFirstAvailableSlot())
                {
                    uint stackSize = Math.Min(template.MaxStackCount, countLeft);
                    AddItem(new Item(owner, template, AssetManager.ItemId.DequeueValue(), stackSize), container, slot, true);
                    
                    countLeft -= stackSize;
                }
            }
            
            if (owner.InWorld)
            {
                // shows new item message in chat
                owner.Session.Send(new ServerActorAction2
                {
                    Action     = ActorAction.ItemCreate,
                    Parameter1 = itemId,
                    Parameter2 = Math.Min(count, count - countLeft)
                });
            }
            
            // TODO: should something be done if there are no slots? Mail item ect?
        }

        /// <summary>
        /// Move an existing item instance to another free container slot.
        /// </summary>
        public void MoveItem(ContainerType source, ushort srcSlot, ContainerType destination, ushort dstSlot)
        {
            Container srcContainer = GetContainer(source);
            Container dstContainer = GetContainer(destination);

            Item srcItem = srcContainer.GetItem(srcSlot);
            if (srcItem == null)
                throw new ArgumentException($"Invalid source item in container: {source}, slot: {srcSlot}");

            if (dstContainer.GetItem(dstSlot) != null)
                throw new ArgumentException($"Can't move item to occupied container: {destination}, slot {dstSlot}!");

            try
            {
                RemoveItem(srcItem, srcContainer);
                AddItem(srcItem, dstContainer, dstSlot);
            }
            catch
            {
                // make sure item is returned to it's original positions
                RollbackItem(srcItem, srcContainer, srcSlot);
                throw;
            }
        }

        private void RollbackItem(Item item, Container originalContainer, ushort originalSlot)
        {
            Debug.Assert(item != null);

            if (item.ContainerType != originalContainer.ContainerType)
            {
                RemoveItem(item, GetContainer(item.ContainerType));
                AddItem(item, originalContainer, originalSlot);
            }

            if (item.Slot != originalSlot)
                item.UpdatePosition(item.ContainerType, originalSlot);
        }

        /// <summary>
        /// Equip a new item instance into it's first valid slot.
        /// </summary>
        public void EquipItem(uint itemId)
        {
            if (!GameTableManager.Items.TryGetValue(itemId, ExdLanguage.En, out ItemEntry template))
                throw new ArgumentException($"Invalid item id {itemId}!");
            
            if (template.EquipSlotCategoryId == 0 || !GameTableManager.EquipSlotCategories.TryGetValue(template.EquipSlotCategoryId, ExdLanguage.None, out EquipSlotCategoryEntry equipSlotTemplate))
                throw new ArgumentException($"Item id {itemId} can't be equipped!");

            // find first free slot for the item (some items such as rings can be equipped into multiple slots)
            Container container = GetContainer(ContainerType.Equipped);
            foreach (ushort slot in equipSlotTemplate.GetEquipSlots())
            {
                if (container.GetItem(slot) != null)
                    continue;

                _EquipItem(new Item(owner, template, AssetManager.ItemId.DequeueValue()), slot);
                break;
            }
        }

        private void EquipItem(Item item, ushort slot)
        {
            if (item.Template.EquipSlotCategoryId == 0 || !GameTableManager.EquipSlotCategories.TryGetValue(item.Template.EquipSlotCategoryId, ExdLanguage.None, out EquipSlotCategoryEntry equipSlotTemplate))
                throw new ArgumentException($"Item id {item.Template.Index} can't be equipped!");

            if (!equipSlotTemplate.GetEquipSlots().Contains(slot))
                throw new ArgumentException($"Item id {item.Template.Index} can't be equipped into slot {slot}!");

            _EquipItem(item, slot);
        }

        private void _EquipItem(Item item, ushort slot)
        {
            Debug.Assert(item != null);
            
            GetContainer(ContainerType.Equipped).AddItem(item, slot);
            SendActorAppearanceUpdate((ContainerEquippedSlot)slot);
        }

        private void UnEquipItem(Item item)
        {
            Debug.Assert(item != null);
            
            GetContainer(ContainerType.Equipped).RemoveItem(item);
            SendActorAppearanceUpdate((ContainerEquippedSlot)item.Slot);
        }

        private void SendActorAppearanceUpdate(ContainerEquippedSlot slot)
        {
            // waist items don't update visual appearance
            if (!owner.InWorld || slot == ContainerEquippedSlot.Waist)
                return;
            
            (ulong MainHand, ulong OffHand) weaponDisplayId = owner.Inventory.GetWeaponDisplayIds();
            owner.SendMessageToVisible(new ServerActorAppearanceUpdate
            {
                MainHandDisplayId     = weaponDisplayId.MainHand,
                OffHandDisplayId      = weaponDisplayId.OffHand,
                VisibleItemDisplayIds = GetVisibleItemDisplayIds()
            });
        }
        
        /// <summary>
        /// Swaps two existing item instances and their positions with each other.
        /// </summary>
        public void SwapItems(ContainerType source, ushort srcSlot, ContainerType destination, ushort dstSlot)
        {
            Container srcContainer = GetContainer(source);
            Container dstContainer = GetContainer(destination);

            Item srcItem = srcContainer.GetItem(srcSlot);
            if (srcItem == null)
                throw new ArgumentException($"Invalid source item in container: {source}, slot: {srcSlot}");

            Item dstItem = dstContainer.GetItem(dstSlot);
            if (dstItem == null)
                throw new ArgumentException($"Invalid destination item in container: {destination}, slot: {dstSlot}");

            try
            {
                RemoveItem(srcItem, srcContainer);
                RemoveItem(dstItem, dstContainer);
                AddItem(srcItem, dstContainer, dstSlot);

                // swapping an equipped item moves it to the appropriate Armoury Chest rather then the source location if not Armoury already
                if (destination == ContainerType.Equipped && (source < ContainerType.ArmouryOffHand || source > ContainerType.ArmouryMainHand))
                {
                    srcContainer = GetContainer(AssetManager.EquipArmouryContainerTypes[(ItemUiCategory)dstItem.Template.UiCategoryId]);
                    srcSlot      = srcContainer.GetFirstAvailableSlot();

                    // TODO: some error about full armoury
                    if (srcSlot == ushort.MaxValue)
                        return;
                }
                
                AddItem(dstItem, srcContainer, srcSlot);
            }
            catch
            {
                // make sure items are returned to their original positions
                RollbackItem(srcItem, srcContainer, srcSlot);
                RollbackItem(dstItem, dstContainer, dstSlot);
                throw;
            }
        }

        /// <summary>
        /// Destroy an existing item instance permanently.
        /// </summary>
        public void DiscardItem(ContainerType source, ushort srcSlot)
        {
            Item srcItem = GetContainer(source).GetItem(srcSlot);
            if (srcItem == null)
                throw new ArgumentException($"Invalid source item in container: {source}, slot: {srcSlot}");

            var transaction = new InventoryTransaction(owner);
            transaction.Add(InventoryAction.Discard, source, srcSlot);
            transaction.Commit();
        }
        
        // TODO: split/stack

        public void Send()
        {
            // TODO: some sequential value, items don't show without it
            uint index = 0u;
            foreach (KeyValuePair<ContainerType, Container> pair in containers)
            {
                pair.Value.SendItemSetup(index);
                owner.Session.Send(new ServerContainerSetup
                {
                    Index     = index++,
                    Type      = pair.Key,
                    ItemCount = pair.Value.Count
                });
            }
        }
    }
}
